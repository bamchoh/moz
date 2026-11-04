namespace Moz.Tests;

/// <summary>Config-level tests for [[client]] protocol="opcua": node ↔ local variable items (phase 14).</summary>
public class OpcUaClientConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-uaclientcfg-" + Guid.NewGuid().ToString("N"));

    public OpcUaClientConfigTests() => Directory.CreateDirectory(_dir);

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

    private static string Messages(Moz.Config.ConfigResult result) => string.Join("\n", result.Errors.Select(e => e.Message));

    private const string Base =
        """
        [[device]]
        name = "plc-mirror"
        holding = 100

        [[struct]]
        name = "Motor"
        [[struct.field]]
        name = "speed"
        type = "int"
        [[struct.field]]
        name = "on"
        type = "bool"

        [[variable]]
        name = "temperature"
        device = "plc-mirror"
        kind = "holding"
        offset = 0
        type = "real"

        [[variable]]
        name = "motor"
        device = "plc-mirror"
        kind = "holding"
        offset = 10
        type = "Motor"

        [[client]]
        name = "ua1"
        protocol = "opcua"
        device = "plc-mirror"
        endpoint = "opc.tcp://192.168.0.30:4840"

        [[client.poll]]
        node = "nsu=urn:moz:ua:ns;s=temp"
        variable = "temperature"

        [[client.push]]
        node = "ns=2;s=speed"
        variable = "motor.speed"
        """;

    [Fact]
    public void OpcUaClient_Loads()
    {
        var result = Load("ok.toml", Base);
        Assert.True(result.Ok, Messages(result));
        var c = result.Config!.Client[0];
        Assert.Equal("opcua", c.Protocol);
        Assert.Equal("opc.tcp://192.168.0.30:4840", c.Endpoint);
        Assert.Equal("", c.RemoteAddr);
        Assert.Null(c.RemoteUnit);
        Assert.Equal("nsu=urn:moz:ua:ns;s=temp", c.Poll[0].Node);
        Assert.Equal("temperature", c.Poll[0].Variable);
        Assert.Null(c.Poll[0].Fc);
        Assert.Null(c.Poll[0].Count);       // the variable defines the size
        Assert.Null(c.Poll[0].LocalKind);
        Assert.Null(c.Poll[0].LocalOffset);
        Assert.Equal("motor.speed", c.Push[0].Variable);
    }

    [Theory]
    [InlineData("endpoint = \"opc.tcp://192.168.0.30:4840\"", "", "client.endpoint is required")]
    [InlineData("endpoint = \"opc.tcp://192.168.0.30:4840\"", "endpoint = \"http://x\"", "client.endpoint value \"http://x\" is invalid")]
    [InlineData("endpoint = \"opc.tcp://192.168.0.30:4840\"", "endpoint = \"opc.tcp://192.168.0.30:4840\"\nremote_addr = \"1.2.3.4:502\"", "client.remote_addr is only valid for")]
    [InlineData("endpoint = \"opc.tcp://192.168.0.30:4840\"", "endpoint = \"opc.tcp://192.168.0.30:4840\"\nremote_unit = 1", "client.remote_unit is only valid for modbus")]
    [InlineData("node = \"nsu=urn:moz:ua:ns;s=temp\"", "node = \"nope\"", "poll.node value \"nope\" is invalid")]
    [InlineData("node = \"nsu=urn:moz:ua:ns;s=temp\"", "", "poll.node is required")]
    [InlineData("variable = \"temperature\"", "variable = \"humidity\"", "poll.variable value \"humidity\" is not declared on device \"plc-mirror\"")]
    [InlineData("variable = \"temperature\"", "variable = \"motor\"", "poll.variable value \"motor\" is not declared on device \"plc-mirror\" (struct variables are addressed by leaf name, e.g. motor.speed)")]
    [InlineData("variable = \"temperature\"", "", "poll.variable is required")]
    [InlineData("variable = \"temperature\"", "variable = \"temperature\"\ncount = 2", "poll.count is only valid for modbus and mc-3e clients")]
    [InlineData("variable = \"temperature\"", "variable = \"temperature\"\nlocal_offset = 0", "poll.local_offset is only valid for modbus and mc-3e clients")]
    [InlineData("variable = \"temperature\"", "variable = \"temperature\"\nlocal_kind = \"holding\"", "poll.local_kind is only valid for modbus and mc-3e clients")]
    [InlineData("variable = \"temperature\"", "variable = \"temperature\"\nfc = 3", "poll.fc is only valid for modbus")]
    [InlineData("variable = \"temperature\"", "variable = \"temperature\"\nremote = \"D1\"", "poll.remote is only valid for mc-3e")]
    [InlineData("variable = \"motor.speed\"", "variable = \"motor.rpm\"", "push.variable value \"motor.rpm\" is not declared")]
    public void OpcUaClient_InvalidKeys_Fail(string find, string replace, string why)
    {
        var result = Load("bad.toml", Base.Replace(find, replace));
        Assert.False(result.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(result));
    }

    [Theory]
    [InlineData("remote_offset = 100", "remote_offset = 100\nnode = \"ns=2;s=x\"", "poll.node is only valid for opcua clients")]
    [InlineData("remote_offset = 100", "remote_offset = 100\nvariable = \"temperature\"", "poll.variable is only valid for opcua clients")]
    [InlineData("remote_addr = \"192.168.0.10:502\"", "remote_addr = \"192.168.0.10:502\"\nendpoint = \"opc.tcp://h:1\"", "client.endpoint is only valid for opcua clients")]
    public void ModbusClient_OpcUaKeys_Fail(string find, string replace, string why)
    {
        const string modbus =
            """
            [[device]]
            name = "plc-mirror"
            holding = 100

            [[variable]]
            name = "temperature"
            device = "plc-mirror"
            kind = "holding"
            offset = 0
            type = "real"

            [[client]]
            name = "p1"
            device = "plc-mirror"
            remote_addr = "192.168.0.10:502"

            [[client.poll]]
            remote_offset = 100
            count = 2
            local_offset = 0
            """;
        var result = Load("modbus-bad.toml", modbus.Replace(find, replace));
        Assert.False(result.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(result));
    }

    // --- secure channels and user (phase 16) ----------------------------------------------

    [Fact]
    public void ClientSecurity_Defaults()
    {
        var c = Load("cdef.toml", Base).Config!.Client[0];
        Assert.Equal("none", c.Security);
        Assert.Null(c.User);
        Assert.Null(c.Password);
        Assert.True(c.AcceptUntrusted);
        Assert.Null(c.Pki);
    }

    [Fact]
    public void ClientSecurity_Loads()
    {
        var toml = Base.Replace("endpoint = \"opc.tcp://192.168.0.30:4840\"",
            "endpoint = \"opc.tcp://192.168.0.30:4840\"\nsecurity = \"sign-encrypt\"\nuser = \"operator\"\npassword = \"op-pass\"\naccept_untrusted = false\npki = \"pki/ua1\"");
        var r = Load("csec.toml", toml);
        Assert.True(r.Ok, Messages(r));
        var c = r.Config!.Client[0];
        Assert.Equal(("sign-encrypt", "operator", "op-pass", false, "pki/ua1"), (c.Security, c.User, c.Password, c.AcceptUntrusted, c.Pki));
    }

    [Fact]
    public void ClientSecurity_OtherPolicy_Loads()
    {
        var toml = Base.Replace("endpoint = \"opc.tcp://192.168.0.30:4840\"", "endpoint = \"opc.tcp://192.168.0.30:4840\"\nsecurity = \"aes256-sha256-rsapss:sign-encrypt\"");
        var r = Load("csecpol.toml", toml);
        Assert.True(r.Ok, Messages(r));
        Assert.Equal("aes256-sha256-rsapss:sign-encrypt", r.Config!.Client[0].Security);
    }

    [Fact]
    public void ClientSecurity_Ua105Policy_Loads()
    {
        var toml = Base.Replace("endpoint = \"opc.tcp://192.168.0.30:4840\"", "endpoint = \"opc.tcp://192.168.0.30:4840\"\nsecurity = \"rsa-dh-chachapoly:sign-encrypt\"");
        var r = Load("csecpol2.toml", toml);
        Assert.True(r.Ok, Messages(r));
        Assert.Equal("rsa-dh-chachapoly:sign-encrypt", r.Config!.Client[0].Security);
    }

    [Theory]
    [InlineData("security = \"tls\"", "client.security value \"tls\" is invalid")]
    [InlineData("security = \"basic256:encrypt\"", "client.security value \"basic256:encrypt\" is invalid")]
    [InlineData("user = \"operator\"", "client.password is required when client.user is set")]
    [InlineData("password = \"x\"", "client.user is required when client.password is set")]
    public void ClientSecurity_Invalid_Fail(string line, string why)
    {
        var toml = Base.Replace("endpoint = \"opc.tcp://192.168.0.30:4840\"", "endpoint = \"opc.tcp://192.168.0.30:4840\"\n" + line);
        var r = Load("csecbad.toml", toml);
        Assert.False(r.Ok, "expected: " + why);
        Assert.Contains(why, Messages(r));
    }

    [Theory]
    [InlineData("security = \"sign\"", "client.security is only valid for opcua clients")]
    [InlineData("user = \"u\"\npassword = \"p\"", "client.user is only valid for opcua clients")]
    [InlineData("accept_untrusted = false", "client.accept_untrusted is only valid for opcua clients")]
    [InlineData("pki = \"x\"", "client.pki is only valid for opcua clients")]
    public void ClientSecurity_OnModbus_Fails(string line, string why)
    {
        const string modbus =
            """
            [[device]]
            name = "plc-mirror"
            holding = 100

            [[client]]
            name = "p1"
            device = "plc-mirror"
            remote_addr = "192.168.0.10:502"

            [[client.poll]]
            remote_offset = 100
            count = 2
            local_offset = 0
            """;
        var r = Load("csecproto.toml", modbus.Replace("remote_addr = \"192.168.0.10:502\"", "remote_addr = \"192.168.0.10:502\"\n" + line));
        Assert.False(r.Ok, "expected: " + why);
        Assert.Contains(why, Messages(r));
    }
}
