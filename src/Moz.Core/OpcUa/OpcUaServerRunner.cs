using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace Moz.OpcUa;

/// <summary>
/// The opcua <see cref="IProtocolRunner"/>: exposes the device's typed
/// variables as OPC UA nodes (karakuri parity: variables become browsable
/// nodes with per-change subscriptions).
/// Spec pitfalls encoded here (both found with the phase-7 spike):
/// 1. <see cref="ServerSecurityPolicy.SecurityMode"/> MUST be set explicitly —
///    the enum default is Invalid and every OPN answer fails silently.
/// 2. The 2.0.1 SDK flows its <see cref="ITelemetryContext"/> through ctors,
///    not ambient configuration.
/// </summary>
public sealed class OpcUaServerRunner : IProtocolRunner
{
    private readonly string _endpoint;
    private readonly Device _device;
    private readonly VariableTable _variables;
    private readonly ILogger _logger;
    private MozUaServer? _server;

    public OpcUaServerRunner(string name, string endpoint, Device device,
        VariableTable variables, ILogger logger)
    {
        Name = name;
        _endpoint = endpoint;
        _device = device;
        _variables = variables;
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>The ApplicationConfiguration after <see cref="RunAsync"/> started (for tests).</summary>
    public ApplicationConfiguration? Config { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var telemetry = OpcUaTelemetry.Telemetry;
        Config = BuildConfig(telemetry);
        await Config.ValidateAsync(ApplicationType.Server, CancellationToken.None);
        var instance = new ApplicationInstance(Config, telemetry);
        await instance.CheckApplicationInstanceCertificatesAsync(true, 2048, CancellationToken.None);

        _server = new MozUaServer(_device, _variables);
        await instance.StartAsync(_server, ct);

        _logger.LogInformation("opcua endpoint listening {Name} on {Endpoint} ({Count} nodes)",
            Name, _endpoint, _variables.Variables.Count);

        try
        {
            await WaitCancellation(ct);
        }
        finally
        {
            try
            {
                await _server.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("opcua stop issue: {Message}", ex.Message);
            }
        }
    }

    private static async Task WaitCancellation(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ApplicationConfiguration BuildConfig(ITelemetryContext telemetry)
    {
        if (!Uri.TryCreate(_endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme != "opc.tcp")
        {
            throw new ArgumentException(
                $"invalid opcua endpoint \"{_endpoint}\" (config validation reports this earlier)");
        }

        var pkiRoot = Path.Combine(AppContext.BaseDirectory, "pki-" + Name);
        var config = new ApplicationConfiguration(telemetry)
        {
            ApplicationName = $"moz-{Name}",
            ApplicationType = ApplicationType.Server,
            ApplicationUri = $"urn:moz:{Name}:server",
            ProductUri = "urn:moz",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = $"CN=moz-{Name}, DC=127.0.0.1",
                    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType,
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "issuer"),
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "trusted"),
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "rejected"),
                },
                AutoAcceptUntrustedCertificates = true,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048,
            },
            // THE pitfall (see class doc): SecurityMode must be explicit.
            ServerConfiguration = new Opc.Ua.ServerConfiguration
            {
                BaseAddresses = [_endpoint],
                SecurityPolicies =
                [
                    new ServerSecurityPolicy
                    {
                        SecurityPolicyUri = SecurityPolicies.None,
                        SecurityMode = MessageSecurityMode.None,
                    },
                ],
                UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
                MinRequestThreadCount = 1,
                MaxRequestThreadCount = 4,
            },
        };
        return config;
    }
}

/// <summary>
/// The UA server subclass wiring moz's node manager. The variable namespace:
/// one folder per server, nodes <c>ns=2;s=&lt;variable name&gt;</c>.
/// </summary>
internal sealed class MozUaServer(Device device, VariableTable variables) : StandardServer(OpcUaTelemetry.Telemetry)
{
    protected override IMasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        // 2.0: async node managers are the FIRST-class list — the sync
        // overload never drives CreateAddressSpaceAsync (spike finding).
        var nodeManager = new MozNodeManager(server, configuration, device, variables,
            OpcUaTelemetry.Telemetry.CreateLogger<MozNodeManager>());
        return new MasterNodeManager(server, configuration, null, [nodeManager], []);
    }
}