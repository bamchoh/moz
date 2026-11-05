namespace Moz.Tests;

/// <summary>Config-level tests for [[server]] protocol = "s7" (phase 22): maps and key placement.</summary>
public class S7ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-s7cfg-" + Guid.NewGuid().ToString("N"));

    public S7ConfigTests() => Directory.CreateDirectory(_dir);

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
        name = "s7-a"
        protocol = "s7"
        device = "plc-a"
        port = 102

        [[server.map]]
        device = "M"
        kind = "words"
        offset = 1000
        count = 128

        [[server.map]]
        device = "i"
        kind = "read_only_words"
        offset = 0

        [[server.map]]
        device = "DB10"
        kind = "words"
        offset = 1500
        count = 100
        """;

    [Fact]
    public void S7_Loads_WithMaps()
    {
        var r = Load("s7.toml", Base);
        Assert.True(r.Ok, Messages(r));
        var s = r.Config!.Server[0];
        Assert.Equal(("s7", 102, 3), (s.Protocol, s.Port, s.Map.Count));
    }

    [Theory]
    [InlineData("kind = \"words\"\noffset = 1000", "kind = \"bits\"\noffset = 1000", "server.map kind value \"bits\" does not match the word device M (words|read_only_words)")]
    [InlineData("device = \"DB10\"", "device = \"T\"", "server.map device value \"T\" is invalid (DB<n>|M|I|Q)")]
    [InlineData("device = \"DB10\"", "device = \"DB0\"", "server.map device value \"DB0\" is invalid")]
    [InlineData("device = \"DB10\"", "device = \"m\"", "duplicate server.map device \"M\"")]
    [InlineData("count = 100", "count = 501", "server.map DB10 offset 1500 (+501 cells) exceeds device \"plc-a\" words length 2000")]
    [InlineData("port = 102", "port = 102\nnode = 1", "server.node is only valid for fins servers")]
    public void S7_InvalidConfig_Fails(string find, string replace, string why)
    {
        var r = Load("s7-bad.toml", Base.Replace(find, replace));
        Assert.False(r.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(r));
    }

    [Fact]
    public void Map_OnModbus_IsRejected_WithS7InMessage()
    {
        var r = Load("s7-map-modbus.toml", Base.Replace("protocol = \"s7\"", "protocol = \"modbus-tcp\""));
        Assert.False(r.Ok);
        Assert.Contains("server.map is only valid for mc-3e, fins, kv-hostlink and s7 servers", Messages(r));
    }
}
