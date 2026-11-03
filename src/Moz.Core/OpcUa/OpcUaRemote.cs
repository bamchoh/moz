using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Client;
using Moz.Devices;
using Moz.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Moz.OpcUa;

/// <summary>One poll or push item: a remote node ↔ a typed local (leaf) variable.</summary>
public readonly record struct OpcUaEntry(ExpandedNodeId Node, Variable Variable)
{
    public LocalRange Local => new(Variable.Table, Variable.Offset, Variable.Cells);
}

/// <summary>
/// An OPC UA server as seen by the generic client loop: one anonymous
/// session (SecurityPolicy None), each poll item one Read of a node's Value
/// attribute, each push item one Write. Values convert through the same
/// codec the moz server uses (<see cref="UaValueCodec"/>) via a scratch
/// device sized to the variable, so the cells handed to the loop are exactly
/// what the variable occupies locally. A bad StatusCode (unknown node, type
/// mismatch, ...) is a rejection: logged as `err=BadXxx`, a poll mirrors
/// nothing, a push is not retried. Service failures and timeouts are
/// IOExceptions for the loop to reset (= reconnect) and retry.
/// </summary>
public sealed class OpcUaRemote : IRemoteDevice
{
    private readonly string _name;
    private readonly string _endpoint;
    private readonly int _timeoutMs;
    private readonly Item[] _polls;
    private readonly Item[] _pushes;
    private readonly ILogger _logger;

    private ApplicationConfiguration? _config;
    private ISession? _session;

    public OpcUaRemote(string name, string endpoint, int timeoutMs, OpcUaEntry[] polls, OpcUaEntry[] pushes, ILogger logger)
    {
        _name = name;
        _endpoint = endpoint;
        _timeoutMs = timeoutMs;
        _polls = polls.Select(e => new Item(e)).ToArray();
        _pushes = pushes.Select(e => new Item(e)).ToArray();
        _logger = logger;
        Polls = polls.Select(e => e.Local).ToArray();
        Pushes = pushes.Select(e => e.Local).ToArray();
    }

    public string Remote => _endpoint;
    public bool IsOpen => _session is { Connected: true };
    public bool IsConnected => IsOpen;
    public IReadOnlyList<LocalRange> Polls { get; }
    public IReadOnlyList<LocalRange> Pushes { get; }

    /// <summary>Node text in any UA notation ("ns=2;s=Temp", "i=2258", "nsu=urn:x;s=Temp"); throws on malformed text.</summary>
    public static ExpandedNodeId ParseNode(string text) => ExpandedNodeId.Parse(text);

    /// <summary>Config validation helper: does the text parse as a node id?</summary>
    public static bool TryParseNode(string text)
    {
        try
        {
            return !ExpandedNodeId.Parse(text).IsNull;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (IsOpen)
        {
            return;
        }
        _config ??= await CreateConfigurationAsync(_name, ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        ISession session;
        try
        {
            var discovery = await DiscoveryClient.CreateAsync(_config, new Uri(_endpoint), new DiagnosticsMasks(), timeoutCts.Token);
            var endpoints = await discovery.GetEndpointsAsync(null, null, [], [], timeoutCts.Token);
            EndpointDescription? description = null;
            foreach (var e in endpoints.Endpoints)
            {
                if (e.SecurityPolicyUri == SecurityPolicies.None)
                {
                    description = e;
                    break;
                }
            }
            if (description is null)
            {
                throw new IOException($"{_endpoint} offers no SecurityPolicy None endpoint (moz connects anonymously without security)");
            }
            var configured = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(_config));
            session = await new DefaultSessionFactory(OpcUaTelemetry.Telemetry).CreateAsync(
                _config, configured, false, false, $"moz-{_name}", 60_000,
                new UserIdentity(new AnonymousIdentityToken()), [], timeoutCts.Token);
            await session.FetchNamespaceTablesAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"connect timed out after {_timeoutMs}ms");
        }
        foreach (var item in _polls.Concat(_pushes))
        {
            item.Node = ExpandedNodeId.ToNodeId(item.Entry.Node, session.NamespaceUris); // null when the namespace is unknown
        }
        _session = session;
    }

    public async Task<bool[]?> ReadBitsAsync(int poll, CancellationToken ct)
    {
        var item = _polls[poll];
        if (!await ReadIntoScratchAsync(item, ct))
        {
            return null;
        }
        var bits = new bool[item.Entry.Variable.Cells];
        item.Scratch.Read(item.ScratchVariable.Table, 0, bits);
        return bits;
    }

    public async Task<ushort[]?> ReadWordsAsync(int poll, CancellationToken ct)
    {
        var item = _polls[poll];
        if (!await ReadIntoScratchAsync(item, ct))
        {
            return null;
        }
        var words = new ushort[item.Entry.Variable.Cells];
        item.Scratch.Read(item.ScratchVariable.Table, 0, words);
        return words;
    }

    public Task WriteBitsAsync(int push, bool[] values, CancellationToken ct)
    {
        var item = _pushes[push];
        item.Scratch.Write(item.ScratchVariable.Table, 0, values);
        return WriteFromScratchAsync(item, ct);
    }

    public Task WriteWordsAsync(int push, ushort[] values, CancellationToken ct)
    {
        var item = _pushes[push];
        item.Scratch.Write(item.ScratchVariable.Table, 0, values);
        return WriteFromScratchAsync(item, ct);
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await CloseAsync();
    }

    public void Close() => CloseAsync().GetAwaiter().GetResult();

    private async Task CloseAsync()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }
        try
        {
            await session.CloseAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // the link is gone anyway
        }
        session.Dispose();
    }

    /// <summary>Read the node's Value into the scratch variable; false (logged) when the remote rejected it or the value does not fit the variable.</summary>
    private async Task<bool> ReadIntoScratchAsync(Item item, CancellationToken ct)
    {
        var target = item.Entry.Node.ToString();
        int qty = item.Entry.Variable.Count;
        if (item.Node is null)
        {
            _logger.Request(_name, "read", target, qty, "BadNodeIdUnknown");
            return false;
        }
        var session = _session ?? throw new InvalidOperationException("link is not open");
        var response = await Timed(ct, async token => await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
            new ArrayOf<ReadValueId>(new[] { new ReadValueId { NodeId = item.Node.Value, AttributeId = Attributes.Value } }), token));
        var value = response.Results[0];
        if (StatusCode.IsBad(value.StatusCode))
        {
            _logger.Request(_name, "read", target, qty, value.StatusCode.ToString());
            return false;
        }
        try
        {
            UaValueCodec.Write(item.ScratchTable, item.ScratchVariable, value.WrappedValue);
        }
        catch (FormatException)
        {
            _logger.Request(_name, "read", target, qty, "BadTypeMismatch");
            return false;
        }
        catch (ArgumentException)
        {
            _logger.Request(_name, "read", target, qty, "BadOutOfRange");
            return false;
        }
        _logger.Request(_name, "read", target, qty, null);
        return true;
    }

    /// <summary>Write the scratch variable's value to the node; a bad per-node status is logged and not retried.</summary>
    private async Task WriteFromScratchAsync(Item item, CancellationToken ct)
    {
        var target = item.Entry.Node.ToString();
        int qty = item.Entry.Variable.Count;
        if (item.Node is null)
        {
            _logger.Request(_name, "write", target, qty, "BadNodeIdUnknown");
            return;
        }
        var session = _session ?? throw new InvalidOperationException("link is not open");
        var variant = UaValueCodec.Read(item.ScratchTable, item.ScratchVariable);
        var response = await Timed(ct, async token => await session.WriteAsync(null,
            new ArrayOf<WriteValue>(new[] { new WriteValue { NodeId = item.Node.Value, AttributeId = Attributes.Value, Value = new DataValue(variant) } }), token));
        var status = response.Results[0];
        _logger.Request(_name, "write", target, qty, StatusCode.IsBad(status) ? status.ToString() : null);
    }

    /// <summary>One service call under the request timeout; a timeout is an IOException (link reset by the loop).</summary>
    private async Task<T> Timed<T>(CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        try
        {
            return await call(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"request timed out after {_timeoutMs}ms");
        }
    }

    /// <summary>
    /// Client application configuration: anonymous, SecurityPolicy None,
    /// auto-accept server certificates; the (required) client certificate
    /// lives in a per-client PKI folder under the temp directory and is
    /// created once.
    /// </summary>
    private static async Task<ApplicationConfiguration> CreateConfigurationAsync(string name, CancellationToken ct)
    {
        var safe = string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        var pki = Path.Combine(Path.GetTempPath(), "moz-ua-client", safe);
        var config = new ApplicationConfiguration(OpcUaTelemetry.Telemetry)
        {
            ApplicationName = $"moz-{safe}",
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:moz:client:{safe}",
            ProductUri = "urn:moz",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN=moz-{safe}, DC=localhost",
                    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType,
                },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "trusted") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048,
            },
            ClientConfiguration = new ClientConfiguration(),
        };
        await config.ValidateAsync(ApplicationType.Client);
        await new ApplicationInstance(config, OpcUaTelemetry.Telemetry).CheckApplicationInstanceCertificatesAsync(true, 2048, ct);
        return config;
    }

    /// <summary>Per-item state: the resolved NodeId and a scratch device + table holding just this variable at offset 0.</summary>
    private sealed class Item
    {
        public Item(OpcUaEntry entry)
        {
            Entry = entry;
            bool isBit = Variable.IsBit(entry.Variable.Type);
            int cells = Math.Max(1, entry.Variable.Cells);
            ScratchVariable = entry.Variable with { Table = isBit ? TableKind.Coil : TableKind.HoldingRegister, Offset = 0 };
            Scratch = new Device("scratch", coils: isBit ? cells : 1, discreteInputs: 1, holdingRegisters: isBit ? 1 : cells, inputRegisters: 1);
            ScratchTable = new VariableTable(Scratch, [ScratchVariable], NullLogger.Instance);
        }

        public OpcUaEntry Entry { get; }
        public NodeId? Node { get; set; }
        public Variable ScratchVariable { get; }
        public Device Scratch { get; }
        public VariableTable ScratchTable { get; }
    }
}
