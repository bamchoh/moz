using Moz.OpcUa;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Moz.Tests;

/// <summary>
/// A minimal SDK client for server-side tests (identity and security
/// variants): one shared client configuration and certificate, sessions on
/// demand. Separate from moz's own OpcUaRemote so the server is exercised by
/// an independent client implementation.
/// </summary>
internal static class UaTestClient
{
    private static readonly SemaphoreSlim s_lock = new(1, 1);
    private static ApplicationConfiguration? s_config;
    private static string? s_pki;

    public static async Task<ApplicationConfiguration> ConfigAsync()
    {
        await s_lock.WaitAsync();
        try
        {
            if (s_config is not null)
            {
                return s_config;
            }
            var pki = Path.Combine(Path.GetTempPath(), "moz-uatest-pki-" + Guid.NewGuid().ToString("N"));
            s_pki = pki;
            var cfg = new ApplicationConfiguration(OpcUaTelemetry.Telemetry)
            {
                ApplicationName = "moz-uatest",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = "urn:moz:test:uatest",
                ProductUri = "urn:moz:test",
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pki, "own"),
                        SubjectName = "CN=moz-uatest, DC=127.0.0.1",
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
            await cfg.ValidateAsync(ApplicationType.Client);
            await new ApplicationInstance(cfg, OpcUaTelemetry.Telemetry).CheckApplicationInstanceCertificatesAsync(true, 2048, CancellationToken.None);
            s_config = cfg;
            return cfg;
        }
        finally
        {
            s_lock.Release();
        }
    }

    /// <summary>The client certificate file (DER, from the directory store) so a test can place it in a server's trusted store.</summary>
    public static async Task<string> CertificateFileAsync()
    {
        await ConfigAsync();
        var certs = Directory.GetFiles(Path.Combine(s_pki!, "own", "certs"), "*.der");
        return certs.Single();
    }

    /// <summary>
    /// Open a session: pick the endpoint with the given policy/mode and
    /// activate with the identity (anonymous when null). Throws
    /// ServiceResultException with the server's status on rejection.
    /// </summary>
    public static async Task<ISession> ConnectAsync(string endpointUrl, IUserIdentity? identity = null,
        string securityPolicy = SecurityPolicies.None, MessageSecurityMode mode = MessageSecurityMode.None)
    {
        var cfg = await ConfigAsync();
        var discovery = await DiscoveryClient.CreateAsync(cfg, new Uri(endpointUrl), new DiagnosticsMasks(), CancellationToken.None);
        var response = await discovery.GetEndpointsAsync(null, null, [], [], CancellationToken.None);
        EndpointDescription? description = null;
        foreach (var e in response.Endpoints)
        {
            if (e.SecurityPolicyUri == securityPolicy && e.SecurityMode == mode)
            {
                description = e;
                break;
            }
        }
        if (description is null)
        {
            throw new InvalidOperationException($"no endpoint with {securityPolicy} / {mode} at {endpointUrl}");
        }
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(cfg));
        var factory = new DefaultSessionFactory(OpcUaTelemetry.Telemetry);
        return await factory.CreateAsync(cfg, endpoint, false, false, "moz-uatest", 15000,
            identity ?? new UserIdentity(new AnonymousIdentityToken()), [], CancellationToken.None);
    }

    /// <summary>Endpoints the server advertises (policy URI + mode).</summary>
    public static async Task<List<(string Policy, MessageSecurityMode Mode)>> EndpointsAsync(string endpointUrl)
    {
        var cfg = await ConfigAsync();
        var discovery = await DiscoveryClient.CreateAsync(cfg, new Uri(endpointUrl), new DiagnosticsMasks(), CancellationToken.None);
        var response = await discovery.GetEndpointsAsync(null, null, [], [], CancellationToken.None);
        var list = new List<(string Policy, MessageSecurityMode Mode)>();
        foreach (var e in response.Endpoints)
        {
            list.Add((e.SecurityPolicyUri ?? "", e.SecurityMode));
        }
        return list;
    }

    public static async Task<StatusCode> WriteAsync(ISession session, NodeId node, Variant value)
    {
        var response = await session.WriteAsync(null,
            new ArrayOf<WriteValue>(new[] { new WriteValue { NodeId = node, AttributeId = Attributes.Value, Value = new DataValue(value) } }),
            CancellationToken.None);
        return response.Results[0];
    }

    public static async Task<DataValue> ReadAsync(ISession session, NodeId node)
    {
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither,
            new ArrayOf<ReadValueId>(new[] { new ReadValueId { NodeId = node, AttributeId = Attributes.Value } }), CancellationToken.None);
        return response.Results[0];
    }

    /// <summary>The moz namespace index on this session.</summary>
    public static async Task<ushort> MozNamespaceAsync(ISession session)
    {
        await session.FetchNamespaceTablesAsync(CancellationToken.None);
        return (ushort)session.NamespaceUris.GetIndexOrAppend(MozNodeManager.NamespaceUri);
    }
}
