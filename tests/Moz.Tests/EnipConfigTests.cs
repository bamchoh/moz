namespace Moz.Tests;

/// <summary>Config-level tests for [[server]] protocol = "enip" (phase 23): no maps, variables are the tags.</summary>
public class EnipConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-enipcfg-" + Guid.NewGuid().ToString("N"));

    public EnipConfigTests() => Directory.CreateDirectory(_dir);

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
        words = 200

        [[variable]]
        name = "temp"
        device = "plc-a"
        kind = "words"
        offset = 0
        type = "real"

        [[server]]
        name = "enip-a"
        protocol = "enip"
        device = "plc-a"
        port = 44818
        """;

    [Theory]
    [InlineData("enip")]
    [InlineData("enip-nj")]
    public void Enip_Loads(string protocol)
    {
        var r = Load("enip.toml", Base.Replace("protocol = \"enip\"", $"protocol = \"{protocol}\""));
        Assert.True(r.Ok, Messages(r));
        Assert.Equal((protocol, 44818), (r.Config!.Server[0].Protocol, r.Config.Server[0].Port));
    }

    [Theory]
    [InlineData("port = 44818", "port = 44818\n\n[[server.map]]\ndevice = \"M\"\nkind = \"words\"\noffset = 0", "server.map is only valid for mc-3e, fins, kv-hostlink and s7 servers")]
    [InlineData("port = 44818", "port = 44818\nnode = 1", "server.node is only valid for fins servers")]
    [InlineData("port = 44818", "port = 44818\nendpoint = \"opc.tcp://127.0.0.1:4840\"", "server.endpoint is only valid for the opcua protocol")]
    public void Enip_InvalidConfig_Fails(string find, string replace, string why)
    {
        var r = Load("enip-bad.toml", Base.Replace(find, replace));
        Assert.False(r.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(r));
    }
}
