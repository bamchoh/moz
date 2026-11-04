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

/// <summary>A secure-channel flavour: no security, or Basic256Sha256 signed / signed and encrypted.</summary>
public enum OpcUaSecurity
{
    None,
    Sign,
    SignAndEncrypt,
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
    OpcUaSecurity Security = OpcUaSecurity.None,
    string? User = null,
    string? Password = null,
    string? Pki = null,
    bool AcceptUntrusted = true)
{
    public static OpcUaClientOptions Default { get; } = new();
}

public static class OpcUaSecurityExtensions
{
    public static (string PolicyUri, MessageSecurityMode Mode) ToSdk(this OpcUaSecurity security) => security switch
    {
        OpcUaSecurity.Sign => (SecurityPolicies.Basic256Sha256, MessageSecurityMode.Sign),
        OpcUaSecurity.SignAndEncrypt => (SecurityPolicies.Basic256Sha256, MessageSecurityMode.SignAndEncrypt),
        _ => (SecurityPolicies.None, MessageSecurityMode.None),
    };

    /// <summary>Config spelling ("none" | "sign" | "sign-encrypt") to the enum; null for anything else.</summary>
    public static OpcUaSecurity? Parse(string text) => text switch
    {
        "none" => OpcUaSecurity.None,
        "sign" => OpcUaSecurity.Sign,
        "sign-encrypt" => OpcUaSecurity.SignAndEncrypt,
        _ => null,
    };
}
