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
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MozUaServer? _server;

    private readonly OpcUaServerOptions _options;

    public OpcUaServerRunner(string name, string endpoint, Device device,
        VariableTable variables, ILogger logger, OpcUaServerOptions? options = null)
    {
        Name = name;
        _endpoint = endpoint;
        _device = device;
        _variables = variables;
        _logger = logger;
        _options = options ?? OpcUaServerOptions.Default;
    }

    public string Name { get; }

    /// <summary>The ApplicationConfiguration after <see cref="RunAsync"/> started (for tests).</summary>
    public ApplicationConfiguration? Config { get; private set; }

    /// <summary>
    /// Completes once the endpoint is listening; faults if startup threw.
    /// Tests (and a future GUI) wait on this instead of polling.
    /// </summary>
    public Task Ready => _ready.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var telemetry = OpcUaTelemetry.Telemetry;
            Config = BuildConfig(telemetry);
            await Config.ValidateAsync(ApplicationType.Server, CancellationToken.None);
            var instance = new ApplicationInstance(Config, telemetry);
            await instance.CheckApplicationInstanceCertificatesAsync(true, 2048, CancellationToken.None);

            _server = new MozUaServer(_device, _variables, _options, _logger);
            await instance.StartAsync(_server, ct);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            throw;
        }
        _ready.TrySetResult();

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

        var pkiRoot = _options.Pki ?? Path.Combine(AppContext.BaseDirectory, "pki-" + Name);
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
                AutoAcceptUntrustedCertificates = _options.AcceptUntrusted,
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 2048,
            },
            // THE pitfall (see class doc): SecurityMode must be explicit.
            ServerConfiguration = new Opc.Ua.ServerConfiguration
            {
                BaseAddresses = [_endpoint],
                SecurityPolicies = _options.Policies.Select(sec =>
                {
                    var (uri, mode) = sec.ToSdk();
                    return new ServerSecurityPolicy { SecurityPolicyUri = uri, SecurityMode = mode };
                }).ToArray(),
                UserTokenPolicies = UserTokenPolicies(_options),
                MinRequestThreadCount = 1,
                MaxRequestThreadCount = 4,
            },
        };
        return config;
    }

    /// <summary>
    /// Anonymous is offered unless denied; UserName is offered when users
    /// exist, with its own security policy so the password travels encrypted
    /// even on a SecurityPolicy None channel.
    /// </summary>
    private static UserTokenPolicy[] UserTokenPolicies(OpcUaServerOptions options)
    {
        var policies = new List<UserTokenPolicy>();
        if (options.Anonymous != OpcUaAccess.Deny)
        {
            policies.Add(new UserTokenPolicy(UserTokenType.Anonymous));
        }
        if (options.Users.Count > 0)
        {
            policies.Add(new UserTokenPolicy(UserTokenType.UserName) { SecurityPolicyUri = SecurityPolicies.Basic256Sha256 });
        }
        return [.. policies];
    }
}

/// <summary>
/// The UA server subclass wiring moz's node manager. The variable namespace:
/// one folder per device, nodes <c>ns=&lt;SDK-assigned&gt;;s=&lt;variable name&gt;</c>.
/// </summary>
internal sealed class MozUaServer(Device device, VariableTable variables, OpcUaServerOptions options, ILogger logger)
    : StandardServer(OpcUaTelemetry.Telemetry)
{
    protected override IMasterNodeManager CreateMasterNodeManager(
        IServerInternal server, ApplicationConfiguration configuration)
    {
        // 2.0: async node managers are the FIRST-class list — the sync
        // overload never drives CreateAddressSpaceAsync (spike finding).
        var nodeManager = new MozNodeManager(server, configuration, device, variables, options,
            OpcUaTelemetry.Telemetry.CreateLogger<MozNodeManager>());
        return new MasterNodeManager(server, configuration, null, [nodeManager], []);
    }

    /// <summary>
    /// Register the user name authenticator (SDK 2.0 identity registry; the
    /// legacy ImpersonateUser event is obsolete). Anonymous sessions are
    /// handled by the SDK's default authenticator and gated by the token
    /// policies (none offered when anonymous is denied).
    /// </summary>
    protected override void OnServerStarted(IServerInternal server)
    {
        base.OnServerStarted(server);
        if (options.Users.Count > 0)
        {
            server.IdentityRegistry.Register(new UserNamePasswordAuthenticator(AuthenticateAsync));
        }
    }

    private ValueTask<IUserIdentity> AuthenticateAsync(UserNameIdentityTokenHandler token, CancellationToken ct)
    {
        var user = options.Find(token.UserName ?? "");
        if (user is null || !PasswordMatches(user.Password, token.DecryptedPassword))
        {
            logger.LogWarning("opcua session rejected: unknown user or wrong password ({User})", token.UserName);
            throw new ServiceResultException(StatusCodes.BadUserAccessDenied);
        }
        logger.LogInformation("opcua session for user {User} ({Access})", user.Name, user.CanWrite ? "write" : "read");
        return ValueTask.FromResult<IUserIdentity>(new UserIdentity(token));
    }

    private static bool PasswordMatches(string expected, ReadOnlySpan<byte> given)
    {
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, given);
    }
}