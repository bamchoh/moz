using Microsoft.Extensions.Logging.Abstractions;
using Moz.Client;
using Moz.Devices;
using Moz.OpcUa;
using Opc.Ua;

namespace Moz.Tests;

/// <summary>
/// OPC UA secure channels: which endpoints the server offers (None /
/// Basic256Sha256 Sign / SignAndEncrypt), certificate trust (auto-accept vs
/// the trusted store), and moz's own client choosing a policy and a user.
/// </summary>
public class OpcUaSecurityTests
{
    private static int s_port = 48720; // own range
    private static readonly SemaphoreSlim s_lock = new(1, 1);

    private sealed record Started(Device Device, VariableTable Variables, Task Run, CancellationTokenSource Cts, ushort Port, string Pki) : IAsyncDisposable
    {
        public string Endpoint => $"opc.tcp://127.0.0.1:{Port}";

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Cts.Dispose();
        }
    }

    private static async Task<Started> StartAsync(OpcUaServerOptions options)
    {
        await s_lock.WaitAsync();
        try
        {
            var port = (ushort)Math.Clamp(Interlocked.Increment(ref s_port), 48721, 48799);
            var pki = options.Pki ?? Path.Combine(Path.GetTempPath(), "moz-uasec-" + Guid.NewGuid().ToString("N"));
            var device = new Device("plc-ua", holdingRegisters: 100, coils: 100);
            var vars = new VariableTable(device, [new Variable("temp", TableKind.HoldingRegister, 0, VariableType.Real)], NullLogger.Instance);
            var runner = new OpcUaServerRunner("ua-sec", $"opc.tcp://127.0.0.1:{port}", device, vars, NullLogger.Instance, options with { Pki = pki });
            var cts = new CancellationTokenSource();
            var run = runner.RunAsync(cts.Token);
            await runner.Ready.WaitAsync(TimeSpan.FromSeconds(30));
            return new Started(device, vars, run, cts, port, pki);
        }
        finally
        {
            s_lock.Release();
        }
    }

    private static readonly OpcUaServerOptions Secure = OpcUaServerOptions.Default with
    {
        Security = [OpcUaSecurity.Sign, OpcUaSecurity.SignAndEncrypt],
    };

    [Fact]
    public async Task Server_AdvertisesConfiguredPolicies_Only()
    {
        await using var server = await StartAsync(Secure);
        var endpoints = await UaTestClient.EndpointsAsync(server.Endpoint);
        Assert.Contains((SecurityPolicies.Basic256Sha256, MessageSecurityMode.Sign), endpoints);
        Assert.Contains((SecurityPolicies.Basic256Sha256, MessageSecurityMode.SignAndEncrypt), endpoints);
        Assert.DoesNotContain(endpoints, e => e.Policy == SecurityPolicies.None);

        await using var plain = await StartAsync(OpcUaServerOptions.Default);
        var plainEndpoints = await UaTestClient.EndpointsAsync(plain.Endpoint);
        Assert.Single(plainEndpoints);
        Assert.Equal((SecurityPolicies.None, MessageSecurityMode.None), plainEndpoints[0]);
    }

    [Fact]
    public async Task SignAndEncrypt_Session_ReadsAndWrites()
    {
        await using var server = await StartAsync(Secure);
        using var session = await UaTestClient.ConnectAsync(server.Endpoint, null, SecurityPolicies.Basic256Sha256, MessageSecurityMode.SignAndEncrypt);
        var ns = await UaTestClient.MozNamespaceAsync(session);
        Assert.True(StatusCode.IsGood(await UaTestClient.WriteAsync(session, new NodeId("temp", ns), new Variant(4.5f))));
        Assert.Equal(4.5f, server.Variables.Read<float>("temp"));
        var dv = await UaTestClient.ReadAsync(session, new NodeId("temp", ns));
        Assert.True(dv.WrappedValue.TryGetValue(out float f) && f == 4.5f);
    }

    [Fact]
    public async Task UntrustedClient_IsRejected_UntilItsCertificateIsTrusted()
    {
        await using var server = await StartAsync(Secure with { AcceptUntrusted = false });
        await Assert.ThrowsAnyAsync<Exception>(() =>
            UaTestClient.ConnectAsync(server.Endpoint, null, SecurityPolicies.Basic256Sha256, MessageSecurityMode.SignAndEncrypt));

        // the server kept the unknown certificate in rejected/; moving it to trusted/ makes it acceptable
        var trusted = Path.Combine(server.Pki, "trusted", "certs");
        Directory.CreateDirectory(trusted);
        var clientCert = await UaTestClient.CertificateFileAsync();
        File.Copy(clientCert, Path.Combine(trusted, Path.GetFileName(clientCert)), overwrite: true);

        using var session = await UaTestClient.ConnectAsync(server.Endpoint, null, SecurityPolicies.Basic256Sha256, MessageSecurityMode.SignAndEncrypt);
        var ns = await UaTestClient.MozNamespaceAsync(session);
        Assert.True(StatusCode.IsGood(await UaTestClient.WriteAsync(session, new NodeId("temp", ns), new Variant(1.0f))));
    }

    [Fact]
    public async Task MozClient_SignEncrypt_WithUser_PollsAndPushes()
    {
        var options = Secure with
        {
            Users = [new OpcUaUser("operator", "op-pass", CanWrite: true)],
            Anonymous = OpcUaAccess.Deny,
        };
        await using var server = await StartAsync(options);
        server.Variables.Write("temp", 12.5f);
        var local = new Device("local", holdingRegisters: 32);
        var mirror = new Variable("mirror", TableKind.HoldingRegister, 0, VariableType.Real);
        var setpoint = new Variable("setpoint", TableKind.HoldingRegister, 4, VariableType.Real);
        var vars = new VariableTable(local, [mirror, setpoint], NullLogger.Instance);
        var provider = new CapturingProvider();
        var remote = new OpcUaRemote("ua", server.Endpoint, 5000,
            [new OpcUaEntry(ExpandedNodeId.Parse("nsu=urn:moz:ua:ns;s=temp"), mirror)],
            [new OpcUaEntry(ExpandedNodeId.Parse("nsu=urn:moz:ua:ns;s=temp"), setpoint)],
            provider.CreateLogger("ua"),
            new OpcUaClientOptions(OpcUaSecurity.SignAndEncrypt, "operator", "op-pass", AcceptUntrusted: true));
        var poller = new ClientPoller("ua", remote, 200, local, provider.CreateLogger("ua"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(4000);
        Assert.True(poller.IsConnected, string.Join("; ", provider.Entries.Select(e => e.Message)));
        Assert.Equal(12.5f, vars.Read<float>("mirror"));
        vars.Write("setpoint", 99.5f);
        await Task.Delay(1500);
        Assert.False(poller.HasPendingPush);
        await cts.CancelAsync();
        await task;
        Assert.Equal(99.5f, server.Variables.Read<float>("temp"));
    }

    [Fact]
    public async Task MozClient_None_AgainstSecureServer_LogsNoEndpoint()
    {
        await using var server = await StartAsync(Secure);
        var local = new Device("local", holdingRegisters: 32);
        var mirror = new Variable("mirror", TableKind.HoldingRegister, 0, VariableType.Real);
        var provider = new CapturingProvider();
        var remote = new OpcUaRemote("ua", server.Endpoint, 2000,
            [new OpcUaEntry(ExpandedNodeId.Parse("nsu=urn:moz:ua:ns;s=temp"), mirror)], [], provider.CreateLogger("ua"));
        var poller = new ClientPoller("ua", remote, 300, local, provider.CreateLogger("ua"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(2500);
        await cts.CancelAsync();
        await task;
        Assert.False(poller.IsConnected);
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id && e.Message.Contains("no SecurityPolicy"));
    }

    // --- more RSA policies (phase 17) ------------------------------------------------------

    [Theory]
    [InlineData("none", OpcUaPolicy.None, MessageSecurityMode.None)]
    [InlineData("sign", OpcUaPolicy.Basic256Sha256, MessageSecurityMode.Sign)]
    [InlineData("sign-encrypt", OpcUaPolicy.Basic256Sha256, MessageSecurityMode.SignAndEncrypt)]
    [InlineData("basic256sha256:sign", OpcUaPolicy.Basic256Sha256, MessageSecurityMode.Sign)]
    [InlineData("aes256-sha256-rsapss:sign-encrypt", OpcUaPolicy.Aes256Sha256RsaPss, MessageSecurityMode.SignAndEncrypt)]
    [InlineData("aes128-sha256-rsaoaep:sign", OpcUaPolicy.Aes128Sha256RsaOaep, MessageSecurityMode.Sign)]
    [InlineData("basic256:sign-encrypt", OpcUaPolicy.Basic256, MessageSecurityMode.SignAndEncrypt)]
    [InlineData("basic128rsa15:sign", OpcUaPolicy.Basic128Rsa15, MessageSecurityMode.Sign)]
    [InlineData("AES256-SHA256-RSAPSS:SIGN", OpcUaPolicy.Aes256Sha256RsaPss, MessageSecurityMode.Sign)]
    public void Security_Parses(string text, OpcUaPolicy policy, MessageSecurityMode mode)
    {
        var parsed = OpcUaSecurity.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal((policy, mode), (parsed.Value.Policy, parsed.Value.Mode));
        Assert.Equal(parsed, OpcUaSecurity.Parse(parsed.Value.ToString())); // round trip through the config spelling
    }

    [Theory]
    [InlineData("aes256-sha256-rsapss")]   // mode missing
    [InlineData("none:sign")]              // None has no mode
    [InlineData("sign:aes256-sha256-rsapss")]
    [InlineData("basic256sha256:encrypt")]
    [InlineData("ecc-nistp256:sign")]      // ECC needs ECC certificates: not offered
    [InlineData("")]
    public void Security_RejectsUnknown(string text)
    {
        Assert.Null(OpcUaSecurity.Parse(text));
    }

    [Fact]
    public void Security_ToSdk_Uris()
    {
        Assert.Equal((SecurityPolicies.Aes256_Sha256_RsaPss, MessageSecurityMode.SignAndEncrypt), OpcUaSecurity.Parse("aes256-sha256-rsapss:sign-encrypt")!.Value.ToSdk());
        Assert.Equal((SecurityPolicies.Aes128_Sha256_RsaOaep, MessageSecurityMode.Sign), OpcUaSecurity.Parse("aes128-sha256-rsaoaep:sign")!.Value.ToSdk());
        Assert.Equal((SecurityPolicies.Basic256, MessageSecurityMode.Sign), OpcUaSecurity.Parse("basic256:sign")!.Value.ToSdk());
        Assert.Equal((SecurityPolicies.Basic128Rsa15, MessageSecurityMode.SignAndEncrypt), OpcUaSecurity.Parse("basic128rsa15:sign-encrypt")!.Value.ToSdk());
        Assert.Equal((SecurityPolicies.None, MessageSecurityMode.None), OpcUaSecurity.None.ToSdk());
    }

    private static readonly OpcUaServerOptions ManyPolicies = OpcUaServerOptions.Default with
    {
        Security =
        [
            OpcUaSecurity.Parse("aes256-sha256-rsapss:sign-encrypt")!.Value,
            OpcUaSecurity.Parse("aes128-sha256-rsaoaep:sign")!.Value,
            OpcUaSecurity.Parse("basic256:sign-encrypt")!.Value,
            OpcUaSecurity.Parse("basic128rsa15:sign")!.Value,
        ],
    };

    [Fact]
    public async Task Server_AdvertisesOtherRsaPolicies()
    {
        await using var server = await StartAsync(ManyPolicies);
        var endpoints = await UaTestClient.EndpointsAsync(server.Endpoint);
        Assert.Contains((SecurityPolicies.Aes256_Sha256_RsaPss, MessageSecurityMode.SignAndEncrypt), endpoints);
        Assert.Contains((SecurityPolicies.Aes128_Sha256_RsaOaep, MessageSecurityMode.Sign), endpoints);
        Assert.Contains((SecurityPolicies.Basic256, MessageSecurityMode.SignAndEncrypt), endpoints);
        Assert.Contains((SecurityPolicies.Basic128Rsa15, MessageSecurityMode.Sign), endpoints);
        Assert.DoesNotContain(endpoints, e => e.Policy == SecurityPolicies.None || e.Policy == SecurityPolicies.Basic256Sha256);
    }

    [Theory]
    [InlineData(SecurityPolicies.Aes256_Sha256_RsaPss, MessageSecurityMode.SignAndEncrypt)]
    [InlineData(SecurityPolicies.Aes128_Sha256_RsaOaep, MessageSecurityMode.Sign)]
    public async Task OtherRsaPolicy_Session_ReadsAndWrites(string policy, MessageSecurityMode mode)
    {
        await using var server = await StartAsync(ManyPolicies);
        using var session = await UaTestClient.ConnectAsync(server.Endpoint, null, policy, mode);
        var ns = await UaTestClient.MozNamespaceAsync(session);
        Assert.True(StatusCode.IsGood(await UaTestClient.WriteAsync(session, new NodeId("temp", ns), new Variant(6.5f))));
        Assert.Equal(6.5f, server.Variables.Read<float>("temp"));
    }

    [Fact]
    public async Task MozClient_Aes128Sign_Polls()
    {
        await using var server = await StartAsync(ManyPolicies);
        server.Variables.Write("temp", 42.0f);
        var local = new Device("local", holdingRegisters: 32);
        var mirror = new Variable("mirror", TableKind.HoldingRegister, 0, VariableType.Real);
        var vars = new VariableTable(local, [mirror], NullLogger.Instance);
        var provider = new CapturingProvider();
        var remote = new OpcUaRemote("ua", server.Endpoint, 5000,
            [new OpcUaEntry(ExpandedNodeId.Parse("nsu=urn:moz:ua:ns;s=temp"), mirror)], [], provider.CreateLogger("ua"),
            new OpcUaClientOptions(OpcUaSecurity.Parse("aes128-sha256-rsaoaep:sign")!.Value));
        var poller = new ClientPoller("ua", remote, 200, local, provider.CreateLogger("ua"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(4000);
        Assert.True(poller.IsConnected, string.Join("; ", provider.Entries.Select(e => e.Message)));
        await cts.CancelAsync();
        await task;
        Assert.Equal(42.0f, vars.Read<float>("mirror"));
    }
}
