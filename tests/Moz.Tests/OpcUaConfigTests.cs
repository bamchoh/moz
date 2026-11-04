namespace Moz.Tests;

/// <summary>Config-level tests for [[server]] protocol="opcua" (phase 7).</summary>
public class OpcUaConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-uacfg-" + Guid.NewGuid().ToString("N"));

    public OpcUaConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Moz.Config.ConfigResult Load(string name, string toml)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, toml);
        return Moz.Config.ConfigLoader.Load(path);
    }

    private const string Base =
        """
        [[device]]
        name = "plc1"

        [[server]]
        name = "ua-a"
        protocol = "opcua"
        device = "plc1"
        endpoint = "opc.tcp://127.0.0.1:48400"
        """;

    [Fact]
    public void ValidOpcUa_Loads()
    {
        var r = Load("ok.toml", Base);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal("opc.tcp://127.0.0.1:48400", r.Config!.Server[0].Endpoint);
    }

    [Fact]
    public void MissingEndpoint_Fails()
    {
        var r = Load("noep.toml", Base.Replace("endpoint = \"opc.tcp://127.0.0.1:48400\"", ""));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Message.Contains("server.endpoint is required"));
    }

    [Theory]
    [InlineData("opc.tcp://127.0.0.1")]         // no port
    [InlineData("opc.tcp://127.0.0.1:0")]       // port 0
    [InlineData("opc.tcp://127.0.0.1:99999")]
    [InlineData("http://127.0.0.1:48400")]      // wrong scheme
    [InlineData("127.0.0.1:48400")]             // not a URL
    [InlineData("opc.tcp://:48400")]            // no host
    public void InvalidEndpoint_Fails(string endpoint)
    {
        var r = Load("bad.toml", Base.Replace("endpoint = \"opc.tcp://127.0.0.1:48400\"", $"endpoint = \"{endpoint}\""));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Message.Contains($"endpoint value \"{endpoint}\" is invalid"));
    }

    [Fact]
    public void EndpointOnTcpProtocol_Fails()
    {
        var toml =
            """
            [[device]]
            name = "plc1"

            [[server]]
            name = "tcp-a"
            protocol = "modbus-tcp"
            device = "plc1"
            endpoint = "opc.tcp://127.0.0.1:48400"
            """;
        var r = Load("mismatch.toml", toml);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Message.Contains("only valid for the opcua protocol"));
    }

    // --- user management (phase 15) ---------------------------------------------------

    private const string Users =
        """
        [[device]]
        name = "plc1"

        [[server]]
        name = "ua-a"
        protocol = "opcua"
        device = "plc1"
        endpoint = "opc.tcp://127.0.0.1:48400"

        [[server.user]]
        name = "operator"
        password = "op-pass"
        access = "write"

        [[server.user]]
        name = "viewer"
        password = "view-pass"
        access = "read"
        """;

    [Fact]
    public void Users_Load_AnonymousDefaultsToDeny()
    {
        var r = Load("users.toml", Users);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var s = r.Config!.Server[0];
        Assert.Equal(2, s.User.Count);
        Assert.Equal(("operator", "op-pass", "write"), (s.User[0].Name, s.User[0].Password, s.User[0].Access));
        Assert.Equal("read", s.User[1].Access);
        Assert.Equal("deny", s.Anonymous);
    }

    [Fact]
    public void NoUsers_AnonymousDefaultsToWrite_AccessDefaultsToWrite()
    {
        var r = Load("nousers.toml", Base);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal("write", r.Config!.Server[0].Anonymous);
        var r2 = Load("defaccess.toml", Users.Replace("access = \"write\"", ""));
        Assert.True(r2.Ok, string.Join("\n", r2.Errors));
        Assert.Equal("write", r2.Config!.Server[0].User[0].Access);
    }

    [Theory]
    [InlineData("name = \"viewer\"", "name = \"operator\"", "duplicate server.user name \"operator\"")]
    [InlineData("name = \"viewer\"", "name = \"\"", "server.user.name is required")]
    [InlineData("password = \"view-pass\"", "password = \"\"", "server.user.password is required")]
    [InlineData("access = \"read\"", "access = \"admin\"", "server.user.access value \"admin\" is invalid")]
    [InlineData("endpoint = \"opc.tcp://127.0.0.1:48400\"", "endpoint = \"opc.tcp://127.0.0.1:48400\"\nanonymous = \"maybe\"", "server.anonymous value \"maybe\" is invalid")]
    [InlineData("protocol = \"opcua\"", "protocol = \"modbus-tcp\"", "server.user is only valid for opcua servers")]
    public void Users_Invalid_Fail(string find, string replace, string why)
    {
        var r = Load("bad.toml", Users.Replace(find, replace));
        Assert.False(r.Ok, "expected: " + why);
        Assert.Contains(why, string.Join("\n", r.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void Anonymous_OnNonOpcUa_Fails()
    {
        var r = Load("anon.toml", Base.Replace("protocol = \"opcua\"", "protocol = \"modbus-tcp\"")
            .Replace("endpoint = \"opc.tcp://127.0.0.1:48400\"", "anonymous = \"read\""));
        Assert.False(r.Ok);
        Assert.Contains("server.anonymous is only valid for opcua servers", string.Join("\n", r.Errors.Select(e => e.Message)));
    }

    // --- secure channels (phase 16) ------------------------------------------------------

    [Fact]
    public void Security_Defaults_NoneAndAcceptUntrusted()
    {
        var r = Load("secdef.toml", Base);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var s = r.Config!.Server[0];
        Assert.Equal(["none"], s.Security);
        Assert.True(s.AcceptUntrusted);
        Assert.Null(s.Pki);
    }

    [Fact]
    public void Security_Loads()
    {
        var r = Load("sec.toml", Base + "\nsecurity = [\"sign\", \"sign-encrypt\"]\naccept_untrusted = false\npki = \"pki/ua-a\"\n");
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        var s = r.Config!.Server[0];
        Assert.Equal(["sign", "sign-encrypt"], s.Security);
        Assert.False(s.AcceptUntrusted);
        Assert.Equal("pki/ua-a", s.Pki);
    }

    [Fact]
    public void Security_OtherPolicies_Load()
    {
        var r = Load("secpol.toml", Base + "\nsecurity = [\"aes256-sha256-rsapss:sign-encrypt\", \"aes128-sha256-rsaoaep:sign\", \"basic128rsa15:sign\", \"sign\", \"rsa-dh-aesgcm:sign-encrypt\", \"rsa-dh-chachapoly:sign\"]\n");
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal(6, r.Config!.Server[0].Security!.Count);
    }

    [Theory]
    [InlineData("security = [\"tls\"]", "server.security value \"tls\" is invalid")]
    [InlineData("security = [\"aes256-sha256-rsapss\"]", "server.security value \"aes256-sha256-rsapss\" is invalid (none|sign|sign-encrypt|<policy>:sign|<policy>:sign-encrypt")]
    [InlineData("security = [\"sign\", \"basic256sha256:sign\"]", "duplicate server.security value \"basic256sha256:sign\"")]
    [InlineData("security = [\"rsa-dh-aesgcm:sign\", \"RSA-DH-AESGCM:sign\"]", "duplicate server.security value \"rsa-dh-aesgcm:sign\"")]
    [InlineData("security = []", "server.security must list at least one")]
    [InlineData("security = [\"none\", \"none\"]", "duplicate server.security value \"none\"")]
    public void Security_Invalid_Fail(string line, string why)
    {
        var r = Load("secbad.toml", Base + "\n" + line + "\n");
        Assert.False(r.Ok, "expected: " + why);
        Assert.Contains(why, string.Join("\n", r.Errors.Select(e => e.Message)));
    }

    [Theory]
    [InlineData("security = [\"sign\"]", "server.security is only valid for opcua servers")]
    [InlineData("accept_untrusted = false", "server.accept_untrusted is only valid for opcua servers")]
    [InlineData("pki = \"x\"", "server.pki is only valid for opcua servers")]
    public void Security_OnNonOpcUa_Fails(string line, string why)
    {
        var toml = Base.Replace("protocol = \"opcua\"", "protocol = \"modbus-tcp\"")
            .Replace("endpoint = \"opc.tcp://127.0.0.1:48400\"", line);
        var r = Load("secproto.toml", toml);
        Assert.False(r.Ok, "expected: " + why);
        Assert.Contains(why, string.Join("\n", r.Errors.Select(e => e.Message)));
    }
}
