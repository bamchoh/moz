using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.OpcUa;
using Opc.Ua;
using Opc.Ua.Client;

using Opc.Ua.Configuration;

namespace Moz.Tests;

/// <summary>
/// OPC UA (phase 7): an in-process UA server exposing the device's typed
/// variables as nodes. External tools (SDK client here, asyncua in manual
/// checks) browse, read, write and subscribe — the device stays the source
/// of truth and Watch pushes changes into the node values.
///
/// THE spike-found pitfall, encoded here as the spec: the server's
/// ServerSecurityPolicy.SecurityMode MUST be set explicitly (None), the enum
/// default is Invalid and every OPN answer would fail.
/// </summary>
public class OpcUaServerTests
{
    private static int s_port = 48420; // tests allocate ports sequentially

    private static readonly IReadOnlyDictionary<string, StructDef> Structs = new Dictionary<string, StructDef>
    {
        ["Motor"] = new("Motor", [new StructField("speed", "int"), new StructField("on", "bool"), new StructField("rpm", "real")]),
    };

    private static SemaphoreSlim s_lock = new(1, 1);

    /// <summary>A started server: Dispose cancels the runner and waits for it to stop.</summary>
    private sealed record Started(Device Device, VariableTable Variables,
        OpcUaServerRunner Runner, Task Run, CancellationTokenSource Cts, ushort Port)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try
            {
                await Run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                // shutdown path
            }
            Cts.Dispose();
        }
    }

    /// <summary>Start moz's UA server over a fresh device with seeded variables.</summary>
    private static async Task<Started> StartAsync(CapturingProvider? provider = null)
    {
        await s_lock.WaitAsync();
        try
        {
            var port = (ushort)Math.Clamp(Interlocked.Increment(ref s_port), 48421, 48499);
            var device = new Device("plc-ua", holdingRegisters: 100, coils: 100);
            device.Seed(TableKind.HoldingRegister, 0, new ushort[] { 0x1234, 0xABCD });
            var vars = new VariableTable(device,
            [
                new Variable("temp", TableKind.HoldingRegister, 0, VariableType.Real),
                new Variable("rpm", TableKind.HoldingRegister, 2, VariableType.Uint),
                new Variable("pump", TableKind.Coil, 0, VariableType.Bool),
                new Variable("pressure", TableKind.HoldingRegister, 4, VariableType.Int),
                new Variable("total", TableKind.HoldingRegister, 6, VariableType.Dint),
                new Variable("serial", TableKind.HoldingRegister, 8, VariableType.Ulint),
                new Variable("big", TableKind.HoldingRegister, 12, VariableType.Lint),
                new Variable("u32", TableKind.HoldingRegister, 16, VariableType.Udint),
                new Variable("samples", TableKind.HoldingRegister, 20, VariableType.Int, Count: 3),
                new Variable("flags", TableKind.Coil, 10, VariableType.Bool, Count: 2),
                new Variable("label", TableKind.HoldingRegister, 24, VariableType.String, Length: 8),
                new Variable("timer", TableKind.HoldingRegister, 28, VariableType.Time),
                new Variable("stamp", TableKind.HoldingRegister, 30, VariableType.Dt),
                .. VariableLayout.Expand("motor", TableKind.HoldingRegister, 34, "Motor", 1, 0, Structs),
                .. VariableLayout.Expand("motors", TableKind.HoldingRegister, 38, "Motor", 2, 0, Structs),
            ], provider is null ? NullLogger.Instance : provider.CreateLogger("test"));

            var logger = provider is null ? NullLogger.Instance : provider.CreateLogger("test");
            var runner = new OpcUaServerRunner("ua-test",
                $"opc.tcp://127.0.0.1:{port}", device, vars, logger);

            var cts = new CancellationTokenSource();
            var run = runner.RunAsync(cts.Token);

            // Ready completes once the endpoint listens (faults if startup threw)
            await runner.Ready.WaitAsync(TimeSpan.FromSeconds(30));
            return new Started(device, vars, runner, run, cts, port);
        }
        finally
        {
            s_lock.Release();
        }
    }

    [Fact]
    public async Task Started_Dispose_StopsServer()
    {
        var started = await StartAsync();
        Assert.False(started.Run.IsCompleted);

        await started.DisposeAsync();

        Assert.True(started.Run.IsCompletedSuccessfully);
        // the endpoint is gone: a fresh TCP connect does not succeed (refused, or
        // hangs on this host's unbound loopback ports — either way not Connected)
        using var probe = new System.Net.Sockets.TcpClient();
        try
        {
            await probe.ConnectAsync("127.0.0.1", started.Port).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException)
        {
        }
        Assert.False(probe.Connected);
    }

    /// <summary>A minimal anonymous UA test client over moz's endpoint.</summary>
    private static async Task<ISession> ConnectAsync(ushort port, ApplicationConfiguration cfg)
    {
        var discovery = await DiscoveryClient.CreateAsync(cfg, new Uri($"opc.tcp://127.0.0.1:{port}"),
            new DiagnosticsMasks(), CancellationToken.None);
        var response = await discovery.GetEndpointsAsync(null, null, [], [],
            CancellationToken.None);
        EndpointDescription epDesc = null!;
        foreach (var e in response.Endpoints)
        {
            if (e.SecurityPolicyUri == SecurityPolicies.None) { epDesc = e; break; }
        }
        var endpoint = new ConfiguredEndpoint(null, epDesc, EndpointConfiguration.Create(cfg));
        var factory = new DefaultSessionFactory(OpcUaTelemetry.Telemetry);
        return await factory.CreateAsync(
            cfg, endpoint, false, false, "moz-test-client", 15000,
            new UserIdentity(new AnonymousIdentityToken()), [], CancellationToken.None);
    }

    /// <summary>Client config + certificate + anonymous session, moz namespace resolved.</summary>
    private static async Task<(ISession Session, ushort Ns)> OpenSessionAsync(Started started)
    {
        var cfg = MakeClientConfig();
        await cfg.ValidateAsync(ApplicationType.Client);
        await new ApplicationInstance(cfg, OpcUaTelemetry.Telemetry)
            .CheckApplicationInstanceCertificatesAsync(true, 2048, CancellationToken.None);
        var session = await ConnectAsync(started.Port, cfg);
        await session.FetchNamespaceTablesAsync(CancellationToken.None);
        var ns = (ushort)session.NamespaceUris.GetIndexOrAppend(MozNodeManager.NamespaceUri);
        return (session, ns);
    }

    /// <summary>One Write service call; returns the per-node status.</summary>
    private static async Task<StatusCode> WriteAsync(ISession session, NodeId node, Variant value)
    {
        var response = await session.WriteAsync(null,
            new ArrayOf<WriteValue>(new[] { new WriteValue
            {
                NodeId = node,
                AttributeId = Attributes.Value,
                Value = new DataValue(value),
            } }), CancellationToken.None);
        return response.Results[0];
    }

    private static ApplicationConfiguration MakeClientConfig()
    {
        var pki = Path.Combine(Path.GetTempPath(), "moz-uaclient-pki-" + Guid.NewGuid().ToString("N"));
        return new ApplicationConfiguration(OpcUaTelemetry.Telemetry)
        {
            ApplicationName = "moz-test-client",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = "urn:moz:test:client",
            ProductUri = "urn:moz:test",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = "CN=moz-test-client, DC=127.0.0.1",
                    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType,
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer"),
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted"),
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected"),
                },
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048,
            },
            ClientConfiguration = new ClientConfiguration(),
        };
    }

    [Fact]
    public async Task Read_Real_And_Uint_MatchDevice()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var expected = started.Variables.Read<float>("temp");
            var dv = await session.ReadValueAsync(new NodeId("temp", ns));
            float actual = dv.WrappedValue.TryGetValue(out float readTemp) ? readTemp
                : throw new InvalidOperationException("temp is not float");
            Assert.Equal(expected, actual);

            var rpm = await session.ReadValueAsync(new NodeId("rpm", ns));
            ushort actualRpm = rpm.WrappedValue.TryGetValue(out ushort readRpm) ? readRpm
                : throw new InvalidOperationException("rpm is not uint16");
            Assert.Equal(started.Variables.Read<uint>("rpm"), actualRpm);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Anonymous in-process client write is accepted: moz is a simulator with
    /// no access control, so the node grants CurrentReadOrWrite to every user.
    /// </summary>
    [Fact]
    public async Task ClientWrite_Real_UpdatesDevice()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var status = await WriteAsync(session, new NodeId("temp", ns), new Variant(42.5f));

            Assert.True(StatusCode.IsGood(status), $"write status {status} sdk log:\n" +
                string.Join("\n", OpcUaTelemetry.SdkLog.TakeLast(20).Select(e => $"{e.Level} {e.Message}")));
            Assert.Equal(42.5f, started.Variables.Read<float>("temp"));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Server-side half of subscriptions: device change → Watch → node.Value.
    /// (The client-notification leg runs on the SDK's own publish machinery;
    /// wired in the attach path and exercised manually with UAExpert.)
    /// </summary>
    [Fact]
    public async Task DeviceChange_UpdatesNodeValue()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var tempNode = new NodeId("temp", ns);

            var before = await session.ReadValueAsync(tempNode);
            float beforeValue = before.WrappedValue.TryGetValue(out float beforeRaw) ? beforeRaw
                : throw new InvalidOperationException("temp is not float");

            started.Variables.Write("temp", 77.5f);

            var after = await session.ReadValueAsync(tempNode);
            float afterValue = after.WrappedValue.TryGetValue(out float afterRaw) ? afterRaw
                : throw new InvalidOperationException("temp is not float");

            Assert.NotEqual(beforeValue, afterValue);
            Assert.Equal(77.5f, afterValue);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    /// <summary>Every scalar type writes through to the device with its UA wire type.</summary>
    [Fact]
    public async Task ClientWrite_EachScalarType_RoundTrip()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var cases = new (string Node, Variant Value, Func<object> ReadBack, object Expected)[]
            {
                ("pressure", new Variant((short)-5), () => started.Variables.Read<int>("pressure"), -5),
                ("rpm", new Variant((ushort)65000), () => started.Variables.Read<uint>("rpm"), 65000u),
                ("total", new Variant(-123456), () => started.Variables.Read<int>("total"), -123456),
                ("u32", new Variant(4000000000u), () => started.Variables.Read<uint>("u32"), 4000000000u),
                ("big", new Variant(long.MinValue), () => started.Variables.Read<long>("big"), long.MinValue),
                ("serial", new Variant(ulong.MaxValue), () => started.Variables.Read<ulong>("serial"), ulong.MaxValue),
                ("pump", new Variant(true), () => started.Variables.Read<bool>("pump"), true),
            };
            foreach (var c in cases)
            {
                var status = await WriteAsync(session, new NodeId(c.Node, ns), c.Value);
                Assert.True(StatusCode.IsGood(status), $"{c.Node}: write status {status}");
                Assert.Equal(c.Expected, c.ReadBack());
            }
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    /// <summary>int/uint are 16-bit on the wire: the served Variant must match DataType Int16/UInt16.</summary>
    [Fact]
    public async Task Read_Int_Uint_AreInt16_UInt16_Variants()
    {
        await using var started = await StartAsync();
        started.Variables.Write("pressure", -7);
        started.Variables.Write("rpm", 1234u);
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var pressure = await session.ReadValueAsync(new NodeId("pressure", ns));
            Assert.True(pressure.WrappedValue.TryGetValue(out short s), $"pressure variant is {pressure.WrappedValue.TypeInfo}");
            Assert.Equal((short)-7, s);

            var rpm = await session.ReadValueAsync(new NodeId("rpm", ns));
            Assert.True(rpm.WrappedValue.TryGetValue(out ushort u), $"rpm variant is {rpm.WrappedValue.TypeInfo}");
            Assert.Equal((ushort)1234, u);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ClientWrite_WrongVariantType_BadTypeMismatch()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var before = started.Variables.Read<uint>("rpm");
            var status = await WriteAsync(session, new NodeId("rpm", ns), new Variant(1.5f));
            Assert.Equal(StatusCodes.BadTypeMismatch, status.Code);
            Assert.Equal(before, started.Variables.Read<uint>("rpm"));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    /// <summary>asyncua/UAExpert send a SourceTimestamp with value writes; moz ignores it.</summary>
    [Fact]
    public async Task ClientWrite_WithSourceTimestamp_IsAccepted()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var response = await session.WriteAsync(null,
                new ArrayOf<WriteValue>(new[] { new WriteValue
                {
                    NodeId = new NodeId("temp", ns),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(11.25f), StatusCodes.Good, DateTimeUtc.From(DateTime.UtcNow)),
                } }), CancellationToken.None);
            var status = response.Results[0];
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            Assert.Equal(11.25f, started.Variables.Read<float>("temp"));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    // --- arrays (count > 1) ------------------------------------------------

    private static async Task<DataValue> ReadAttributeAsync(ISession session, NodeId node, uint attribute)
    {
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
            new ArrayOf<ReadValueId>(new[] { new ReadValueId { NodeId = node, AttributeId = attribute } }),
            CancellationToken.None);
        return response.Results[0];
    }

    [Fact]
    public async Task ArrayVariable_Read_IsOneDimension()
    {
        await using var started = await StartAsync();
        started.Variables.WriteAll("samples", new[] { 10, -20, 30 });
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var node = new NodeId("samples", ns);
            var rank = await ReadAttributeAsync(session, node, Attributes.ValueRank);
            Assert.True(rank.WrappedValue.TryGetValue(out int valueRank));
            Assert.Equal(ValueRanks.OneDimension, valueRank);

            var dv = await session.ReadValueAsync(node);
            Assert.True(dv.WrappedValue.TryGetValue(out ArrayOf<short> arr), $"samples variant is {dv.WrappedValue.TypeInfo}");
            Assert.Equal(new short[] { 10, -20, 30 }, arr.ToArray());
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ArrayVariable_WriteWholeArray_UpdatesDevice()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var status = await WriteAsync(session, new NodeId("samples", ns),
                new Variant(new ArrayOf<short>(new short[] { 1, 2, 3 })));
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            Assert.Equal(new[] { 1, 2, 3 }, started.Variables.ReadAll<int>("samples"));

            status = await WriteAsync(session, new NodeId("flags", ns),
                new Variant(new ArrayOf<bool>(new[] { true, false })));
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            Assert.Equal(new[] { true, false }, started.Variables.ReadAll<bool>("flags"));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ArrayVariable_WrongLength_BadOutOfRange()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var status = await WriteAsync(session, new NodeId("samples", ns),
                new Variant(new ArrayOf<short>(new short[] { 1, 2 })));
            Assert.Equal(StatusCodes.BadOutOfRange, status.Code);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ArrayVariable_ScalarToArray_BadTypeMismatch()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var status = await WriteAsync(session, new NodeId("samples", ns), new Variant((short)1));
            Assert.Equal(StatusCodes.BadTypeMismatch, status.Code);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    // --- subscriptions --------------------------------------------------------

    /// <summary>
    /// The client leg of subscriptions: a MonitoredItem on temp receives the
    /// new value through the server's publish pipeline after a device write.
    /// </summary>
    [Fact]
    public async Task Subscription_DeviceWrite_NotifiesClient()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var received = new TaskCompletionSource<float>(TaskCreationOptions.RunContinuationsAsynchronously);
            var subscription = new Subscription(OpcUaTelemetry.Telemetry, new Opc.Ua.Client.SubscriptionOptions
            {
                PublishingInterval = 100,
                PublishingEnabled = true,
            });
            var item = new MonitoredItem(OpcUaTelemetry.Telemetry, new MonitoredItemOptions
            {
                StartNodeId = new NodeId("temp", ns),
                AttributeId = Attributes.Value,
                SamplingInterval = 50,
            });
            item.Notification += (_, e) =>
            {
                if (e.NotificationValue is MonitoredItemNotification n
                    && n.Value.WrappedValue.TryGetValue(out float f) && f == 55.5f)
                {
                    received.TrySetResult(f);
                }
            };
            subscription.AddItem(item);
            session.AddSubscription(subscription);
            await subscription.CreateAsync(CancellationToken.None);

            started.Variables.Write("temp", 55.5f);

            var value = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(55.5f, value);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    // --- string / time types (phase 8) ---------------------------------------------

    [Fact]
    public async Task String_Node_ReadWrite()
    {
        await using var started = await StartAsync();
        started.Variables.Write("label", "abc");
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var node = new NodeId("label", ns);
            var dv = await session.ReadValueAsync(node);
            Assert.True(dv.WrappedValue.TryGetValue(out string? s), $"label variant is {dv.WrappedValue.TypeInfo}");
            Assert.Equal("abc", s);

            var status = await WriteAsync(session, node, new Variant("hello"));
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            Assert.Equal("hello", started.Variables.Read<string>("label"));

            status = await WriteAsync(session, node, new Variant("123456789")); // 9 bytes > 8
            Assert.Equal(StatusCodes.BadOutOfRange, status.Code);
            Assert.Equal("hello", started.Variables.Read<string>("label"));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Time_Node_IsInt32Ms()
    {
        await using var started = await StartAsync();
        started.Variables.Write("timer", -1500);
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var node = new NodeId("timer", ns);
            var dv = await session.ReadValueAsync(node);
            Assert.True(dv.WrappedValue.TryGetValue(out int ms), $"timer variant is {dv.WrappedValue.TypeInfo}");
            Assert.Equal(-1500, ms);

            var status = await WriteAsync(session, node, new Variant(2500));
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            Assert.Equal(2500, started.Variables.Read<int>("timer"));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dt_Node_IsDateTime_RoundTrip()
    {
        await using var started = await StartAsync();
        started.Variables.Write("stamp", 1_793_525_400ul); // 2026-11-01T09:30:00Z
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var node = new NodeId("stamp", ns);
            var dv = await session.ReadValueAsync(node);
            Assert.True(dv.WrappedValue.TryGetValue(out DateTimeUtc d), $"stamp variant is {dv.WrappedValue.TypeInfo}");
            Assert.Equal(new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc), d.ToDateTime());

            var status = await WriteAsync(session, node,
                new Variant(DateTimeUtc.From(new DateTime(2026, 11, 2, 0, 0, 1, DateTimeKind.Utc))));
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            Assert.Equal(1_793_577_601ul, started.Variables.Read<ulong>("stamp"));
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    // --- structs (phase 8) ------------------------------------------------------------

    private static async Task<List<ReferenceDescription>> BrowseChildrenAsync(ISession session, NodeId node)
    {
        var response = await session.BrowseAsync(null, null, 0,
            new ArrayOf<BrowseDescription>(new[] { new BrowseDescription
            {
                NodeId = node,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.All,
            } }), CancellationToken.None);
        var result = response.Results[0];
        if (StatusCode.IsBad(result.StatusCode))
        {
            throw new InvalidOperationException($"browse {node} failed: {result.StatusCode}");
        }
        return result.References.ToList();
    }

    [Fact]
    public async Task Struct_Variable_BrowsesAsFolder_LeafNodesReadWrite()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var deviceFolder = (await BrowseChildrenAsync(session, ObjectIds.ObjectsFolder))
                .Single(r => r.BrowseName.Name == "plc-ua");
            var deviceChildren = await BrowseChildrenAsync(session, ExpandedNodeId.ToNodeId(deviceFolder.NodeId, session.NamespaceUris));
            var motor = deviceChildren.Single(r => r.BrowseName.Name == "motor");
            Assert.Equal(NodeClass.Object, motor.NodeClass);
            Assert.DoesNotContain(deviceChildren, r => r.BrowseName.Name == "motor.speed"); // leaves live under the folder

            var motorChildren = await BrowseChildrenAsync(session, ExpandedNodeId.ToNodeId(motor.NodeId, session.NamespaceUris));
            Assert.Equal(["speed", "on", "rpm"], motorChildren.Select(r => r.BrowseName.Name));
            var speed = motorChildren.Single(r => r.BrowseName.Name == "speed");
            Assert.Equal(new NodeId("motor.speed", ns), ExpandedNodeId.ToNodeId(speed.NodeId, session.NamespaceUris));

            var status = await WriteAsync(session, new NodeId("motor.speed", ns), new Variant((short)42));
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            Assert.Equal(42, started.Variables.Read<int>("motor.speed"));

            status = await WriteAsync(session, new NodeId("motor.on", ns), new Variant(true));
            Assert.True(StatusCode.IsGood(status), $"write status {status}");
            var on = await session.ReadValueAsync(new NodeId("motor.on", ns));
            Assert.True(on.WrappedValue.TryGetValue(out bool b) && b);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StructArray_OneFolderPerElement()
    {
        await using var started = await StartAsync();
        var (session, ns) = await OpenSessionAsync(started);
        try
        {
            var motors = await BrowseChildrenAsync(session, new NodeId("motors", ns));
            Assert.Equal(["[0]", "[1]"], motors.Select(r => r.BrowseName.Name));
            var second = await BrowseChildrenAsync(session, new NodeId("motors[1]", ns));
            Assert.Equal(["speed", "on", "rpm"], second.Select(r => r.BrowseName.Name));

            started.Variables.Write("motors[1].rpm", 3.5f);
            var dv = await session.ReadValueAsync(new NodeId("motors[1].rpm", ns));
            Assert.True(dv.WrappedValue.TryGetValue(out float f));
            Assert.Equal(3.5f, f);
        }
        finally
        {
            await session.CloseAsync(CancellationToken.None);
        }
    }
}
