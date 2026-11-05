namespace Moz.Tests;

/// <summary>Config-level tests for [[server]] protocol = "kv-hostlink" (phase 21): maps and key placement.</summary>
public class KvConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-kvcfg-" + Guid.NewGuid().ToString("N"));

    public KvConfigTests() => Directory.CreateDirectory(_dir);

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
        bits = 1000
        read_only_bits = 100
        words = 2000

        [[server]]
        name = "kv-a"
        protocol = "kv-hostlink"
        device = "plc-a"
        port = 8501

        [[server.map]]
        device = "MR"
        kind = "read_only_bits"
        offset = 0

        [[server.map]]
        device = "em"
        kind = "words"
        offset = 1000
        count = 100
        """;

    [Fact]
    public void Kv_Loads_WithMaps()
    {
        var r = Load("kv.toml", Base);
        Assert.True(r.Ok, Messages(r));
        var s = r.Config!.Server[0];
        Assert.Equal(("kv-hostlink", 8501, 2), (s.Protocol, s.Port, s.Map.Count));
    }

    [Theory]
    [InlineData("kind = \"read_only_bits\"", "kind = \"words\"", "server.map kind value \"words\" does not match the bit device MR (bits|read_only_bits)")]
    [InlineData("device = \"em\"", "device = \"X\"", "server.map device value \"X\" is invalid (DM|EM|FM|W|ZF|R|MR|LR|B)")]
    [InlineData("device = \"em\"", "device = \"mr\"", "duplicate server.map device \"MR\"")]
    [InlineData("count = 100", "count = 1001", "server.map EM offset 1000 (+1001 cells) exceeds device \"plc-a\" words length 2000")]
    [InlineData("port = 8501", "port = 8501\nnode = 1", "server.node is only valid for fins servers")]
    public void Kv_InvalidConfig_Fails(string find, string replace, string why)
    {
        var r = Load("kv-bad.toml", Base.Replace(find, replace));
        Assert.False(r.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(r));
    }

    [Fact]
    public void Map_OnModbus_IsRejected_WithKvInMessage()
    {
        var r = Load("kv-map-modbus.toml", Base.Replace("protocol = \"kv-hostlink\"", "protocol = \"modbus-tcp\""));
        Assert.False(r.Ok);
        Assert.Contains("server.map is only valid for mc-3e, fins, kv-hostlink and s7 servers", Messages(r));
    }
}
