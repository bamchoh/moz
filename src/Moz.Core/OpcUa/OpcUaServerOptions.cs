using Opc.Ua;

namespace Moz.OpcUa;

/// <summary>What a session may do with the variable nodes.</summary>
public enum OpcUaAccess
{
    /// <summary>The session is not accepted at all (anonymous only).</summary>
    Deny,
    Read,
    Write,
}

/// <summary>The RSA security policies moz offers / connects with (ECC policies need ECC certificates and are not offered).</summary>
public enum OpcUaPolicy
{
    None,
    Basic128Rsa15,
    Basic256,
    Basic256Sha256,
    Aes128Sha256RsaOaep,
    Aes256Sha256RsaPss,
}

/// <summary>
/// One endpoint flavour: a security policy and a message security mode.
/// Config spelling: "none", "sign" / "sign-encrypt" (Basic256Sha256), or
/// "&lt;policy&gt;:sign" / "&lt;policy&gt;:sign-encrypt" with policy =
/// basic128rsa15 | basic256 | basic256sha256 | aes128-sha256-rsaoaep |
/// aes256-sha256-rsapss.
/// </summary>
public readonly record struct OpcUaSecurity(OpcUaPolicy Policy, MessageSecurityMode Mode)
{
    public static OpcUaSecurity None => new(OpcUaPolicy.None, MessageSecurityMode.None);
    public static OpcUaSecurity Sign => new(OpcUaPolicy.Basic256Sha256, MessageSecurityMode.Sign);
    public static OpcUaSecurity SignAndEncrypt => new(OpcUaPolicy.Basic256Sha256, MessageSecurityMode.SignAndEncrypt);

    /// <summary>The accepted spellings, for error messages.</summary>
    public const string Spellings = "none|sign|sign-encrypt|<policy>:sign|<policy>:sign-encrypt; policy = basic128rsa15|basic256|basic256sha256|aes128-sha256-rsaoaep|aes256-sha256-rsapss";

    private static readonly (string Name, OpcUaPolicy Policy, string Uri)[] Policies =
    [
        ("basic128rsa15", OpcUaPolicy.Basic128Rsa15, SecurityPolicies.Basic128Rsa15),
        ("basic256", OpcUaPolicy.Basic256, SecurityPolicies.Basic256),
        ("basic256sha256", OpcUaPolicy.Basic256Sha256, SecurityPolicies.Basic256Sha256),
        ("aes128-sha256-rsaoaep", OpcUaPolicy.Aes128Sha256RsaOaep, SecurityPolicies.Aes128_Sha256_RsaOaep),
        ("aes256-sha256-rsapss", OpcUaPolicy.Aes256Sha256RsaPss, SecurityPolicies.Aes256_Sha256_RsaPss),
    ];

    /// <summary>Config spelling to a flavour; null for anything else (case-insensitive).</summary>
    public static OpcUaSecurity? Parse(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        switch (lower)
        {
            case "none":
                return None;
            case "sign":
                return Sign;
            case "sign-encrypt":
                return SignAndEncrypt;
        }
        int colon = lower.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }
        var policyName = lower[..colon];
        var mode = lower[(colon + 1)..] switch
        {
            "sign" => MessageSecurityMode.Sign,
            "sign-encrypt" => MessageSecurityMode.SignAndEncrypt,
            _ => MessageSecurityMode.Invalid,
        };
        int found = Array.FindIndex(Policies, x => x.Name == policyName);
        return found < 0 || mode == MessageSecurityMode.Invalid ? null : new OpcUaSecurity(Policies[found].Policy, mode);
    }

    /// <summary>The SDK policy URI and mode; policy None always means mode None, so `default` is a valid "no security".</summary>
    public (string PolicyUri, MessageSecurityMode Mode) ToSdk() => Policy == OpcUaPolicy.None
        ? (SecurityPolicies.None, MessageSecurityMode.None)
        : (Entry(Policy).Uri, Mode);

    /// <summary>Canonical config spelling ("basic256sha256:sign", "none").</summary>
    public override string ToString() => Policy == OpcUaPolicy.None
        ? "none"
        : $"{Entry(Policy).Name}:{(Mode == MessageSecurityMode.Sign ? "sign" : "sign-encrypt")}";

    private static (string Name, OpcUaPolicy Policy, string Uri) Entry(OpcUaPolicy policy) =>
        Policies[Array.FindIndex(Policies, x => x.Policy == policy)];
}

/// <summary>One [[server.user]]: name, password (compared as given) and whether writes are allowed.</summary>
public sealed record OpcUaUser(string Name, string Password, bool CanWrite);

/// <summary>
/// Settings of one OPC UA server: user management (named users and the
/// anonymous policy), the endpoints offered (security policies), the PKI
/// folder and whether unknown client certificates are accepted. With no
/// users, anonymous sessions write (the original behaviour); once users are
/// declared, anonymous defaults to Deny.
/// </summary>
public sealed record OpcUaServerOptions(
    IReadOnlyList<OpcUaUser> Users,
    OpcUaAccess Anonymous,
    IReadOnlyList<OpcUaSecurity>? Security = null,
    string? Pki = null,
    bool AcceptUntrusted = true)
{
    public static OpcUaServerOptions Default { get; } = new([], OpcUaAccess.Write);

    /// <summary>The endpoints to offer; None when nothing was configured.</summary>
    public IReadOnlyList<OpcUaSecurity> Policies => Security is { Count: > 0 } ? Security : [OpcUaSecurity.None];

    public OpcUaUser? Find(string name) => Users.FirstOrDefault(u => u.Name == name);

    /// <summary>Write permission of a session identity (null = anonymous).</summary>
    public bool CanWrite(IUserIdentity? identity)
    {
        if (identity is null || identity.TokenType == UserTokenType.Anonymous)
        {
            return Anonymous == OpcUaAccess.Write;
        }
        return Find(identity.DisplayName)?.CanWrite ?? false;
    }
}

/// <summary>
/// Settings of moz's OPC UA client: the endpoint flavour to pick, an
/// optional user name token, the PKI folder (default: a per-client folder
/// under the temp directory) and whether the server certificate is accepted
/// without being in the trusted store.
/// </summary>
public sealed record OpcUaClientOptions(
    OpcUaSecurity Security = default,
    string? User = null,
    string? Password = null,
    string? Pki = null,
    bool AcceptUntrusted = true)
{
    public static OpcUaClientOptions Default { get; } = new();
}
