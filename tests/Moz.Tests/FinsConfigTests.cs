namespace Moz.Tests;

/// <summary>Config-level tests for [[server]] protocol = "fins-tcp" / "fins-udp" (phase 19): node, maps, key placement.</summary>
public class FinsConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-finscfg-" + Guid.NewGuid().ToString("N"));

    public FinsConfigTests() => Directory.CreateDirectory(_dir);

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
        name = "plc-a"
        words = 2000
        read_only_words = 100

        [[server]]
        name = "fins-a"
        protocol = "fins-tcp"
        device = "plc-a"
        port = 9600
        node = 10

        [[server.map]]
        device = "CIO"
        kind = "words"
        offset = 1000
        count = 100

        [[server.map]]
        device = "e0"
        kind = "read_only_words"
        offset = 0
        """;

    [Fact]
    public void FinsTcp_Loads_WithNodeAndMaps()
    {
        var r = Load("fins.toml", Base);
        Assert.True(r.Ok, Messages(r));
        var s = r.Config!.Server[0];
        Assert.Equal(("fins-tcp", 9600, 10, 2), (s.Protocol, s.Port, s.Node, s.Map.Count));
        Assert.Equal("e0", s.Map[1].Device);
    }

    [Fact]
    public void FinsUdp_Loads_WithoutNode()
    {
        var r = Load("fins-udp.toml", Base.Replace("protocol = \"fins-tcp\"", "protocol = \"fins-udp\"").Replace("node = 10\n", ""));
        Assert.True(r.Ok, Messages(r));
        Assert.Null(r.Config!.Server[0].Node);
    }

    [Theory]
    [InlineData("node = 10", "node = 0", "server.node value 0 is out of range 1..254")]
    [InlineData("node = 10", "node = 255", "server.node value 255 is out of range 1..254")]
    [InlineData("protocol = \"fins-tcp\"", "protocol = \"modbus-tcp\"", "server.node is only valid for fins servers")]
    [InlineData("kind = \"words\"", "kind = \"bits\"", "server.map kind value \"bits\" does not match the word device CIO (words|read_only_words)")]
    [InlineData("device = \"CIO\"", "device = \"T\"", "server.map device value \"T\" is invalid (DM|CIO|W|H|A|E0..E18)")]
    [InlineData("device = \"e0\"", "device = \"cio\"", "duplicate server.map device \"CIO\"")]
    [InlineData("count = 100", "count = 1001", "server.map CIO offset 1000 (+1001 cells) exceeds device \"plc-a\" words length 2000")]
    public void Fins_InvalidConfig_Fails(string find, string replace, string why)
    {
        var r = Load("fins-bad.toml", Base.Replace(find, replace));
        Assert.False(r.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(r));
    }

    [Fact]
    public void Map_OnModbus_IsRejected_WithFinsInMessage()
    {
        var r = Load("fins-map-modbus.toml", Base.Replace("protocol = \"fins-tcp\"", "protocol = \"modbus-tcp\"").Replace("node = 10\n", ""));
        Assert.False(r.Ok);
        Assert.Contains("server.map is only valid for mc-3e, fins, kv-hostlink and s7 servers", Messages(r));
    }
}
