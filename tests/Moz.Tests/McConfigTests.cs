namespace Moz.Tests;

/// <summary>Config-level tests for [[client]] protocol="mc-3e" and the protocol-specific poll/push keys (phase 12).</summary>
public class McConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-mccfg-" + Guid.NewGuid().ToString("N"));

    public McConfigTests() => Directory.CreateDirectory(_dir);

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
        coils = 100
        holding = 100

        [[client]]
        name = "mc1"
        protocol = "mc-3e"
        device = "plc-mirror"
        remote_addr = "192.168.0.20:5000"

        [[client.poll]]
        remote = "D100"
        count = 3
        local_offset = 0

        [[client.push]]
        remote = "M0"
        count = 8
        local_offset = 10
        """;

    [Fact]
    public void McClient_Loads_WithDerivedLocalKinds_AndRoutingDefaults()
    {
        var result = Load("ok.toml", Base);
        Assert.True(result.Ok, Messages(result));
        var c = result.Config!.Client[0];
        Assert.Equal("mc-3e", c.Protocol);
        Assert.Equal(0, c.Network);
        Assert.Equal(255, c.Pc);
        Assert.Equal(0x03FF, c.ModuleIo);
        Assert.Equal(0, c.ModuleStation);
        Assert.Null(c.RemoteUnit);
        Assert.Equal("D100", c.Poll[0].Remote);
        Assert.Null(c.Poll[0].Fc);
        Assert.Equal("holding", c.Poll[0].LocalKind);   // D is a word device
        Assert.Equal("coil", c.Push[0].LocalKind);      // M is a bit device
        Assert.Null(c.Push[0].Fc);
    }

    [Fact]
    public void ModbusClient_StillGetsFcDefaults()
    {
        var toml = Base.Replace("protocol = \"mc-3e\"", "protocol = \"modbus-tcp\"")
            .Replace("remote = \"D100\"", "remote_offset = 100")
            .Replace("remote = \"M0\"", "fc = 15\nremote_offset = 0");
        var result = Load("modbus.toml", toml);
        Assert.True(result.Ok, Messages(result));
        var c = result.Config!.Client[0];
        Assert.Equal(1, c.RemoteUnit);
        Assert.Equal(3, c.Poll[0].Fc);
        Assert.Equal("holding", c.Poll[0].LocalKind);
        Assert.Equal(15, c.Push[0].Fc);
        Assert.Equal("coil", c.Push[0].LocalKind);
    }

    [Theory]
    [InlineData("remote = \"D100\"", "remote = \"Q5\"", "poll.remote value \"Q5\" is invalid")]
    [InlineData("remote = \"D100\"", "", "poll.remote is required")]
    [InlineData("remote = \"D100\"", "remote = \"D100\"\nfc = 3", "poll.fc is only valid for modbus")]
    [InlineData("remote = \"D100\"", "remote = \"D100\"\nremote_offset = 1", "poll.remote_offset is only valid for modbus")]
    [InlineData("count = 3", "count = 961", "poll.count value 961 is out of range 1..960")]
    [InlineData("count = 8", "count = 7169", "push.count value 7169 is out of range 1..7168")]
    [InlineData("remote = \"D100\"", "remote = \"D16777214\"", "24-bit")]
    [InlineData("local_offset = 10", "local_offset = 10\nlocal_kind = \"holding\"", "push.local_kind value \"holding\" does not match")]
    [InlineData("local_offset = 0", "local_offset = 0\nlocal_kind = \"discrete\"", "poll.local_kind value \"discrete\" does not match")]
    [InlineData("local_offset = 0", "local_offset = 98", "poll local_offset 98 (+3 cells) exceeds device")]
    [InlineData("remote_addr = \"192.168.0.20:5000\"", "remote_addr = \"192.168.0.20:5000\"\nremote_unit = 1", "client.remote_unit is only valid for modbus")]
    [InlineData("remote_addr = \"192.168.0.20:5000\"", "remote_addr = \"192.168.0.20:5000\"\nnetwork = 256", "client.network value 256 is out of range 0..255")]
    [InlineData("remote_addr = \"192.168.0.20:5000\"", "remote_addr = \"192.168.0.20:5000\"\nmodule_io = 65536", "client.module_io value 65536 is out of range 0..65535")]
    [InlineData("remote_addr = \"192.168.0.20:5000\"", "remote_addr = \"192.168.0.20:5000\"\nserial_port = \"COM1\"", "client.serial_port is only valid for serial protocols")]
    [InlineData("remote_addr = \"192.168.0.20:5000\"", "", "client.remote_addr is required")]
    public void McClient_InvalidKeys_Fail(string find, string replace, string why)
    {
        var result = Load("bad.toml", Base.Replace(find, replace));
        Assert.False(result.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(result));
    }

    [Theory]
    [InlineData("remote_offset = 100", "remote_offset = 100\nremote = \"D1\"", "poll.remote is only valid for mc-3e")]
    [InlineData("remote_addr = \"192.168.0.20:5000\"", "remote_addr = \"192.168.0.20:5000\"\nnetwork = 1", "client.network is only valid for mc-3e")]
    public void ModbusClient_McKeys_Fail(string find, string replace, string why)
    {
        var toml = Base.Replace("protocol = \"mc-3e\"", "protocol = \"modbus-tcp\"")
            .Replace("remote = \"D100\"", "remote_offset = 100")
            .Replace("remote = \"M0\"", "fc = 15\nremote_offset = 0");
        var result = Load("modbus-bad.toml", toml.Replace(find, replace));
        Assert.False(result.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(result));
    }
}
