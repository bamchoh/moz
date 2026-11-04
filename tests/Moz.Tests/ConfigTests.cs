using Moz.Config;

namespace Moz.Tests;

/// <summary>
/// Strictness + validation contract of the TOML config loader: unknown keys
/// must NEVER pass silently (the whole point of the strictness pass).
/// </summary>
public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string ValidT = /*lang=json,strict*/
        """
        [[device]]
        name = "plc1"
        bits = 100

        [[server]]
        name = "sim-a"
        protocol = "modbus-tcp"
        device = "plc1"
        port = 50200
        """;

    [Fact]
    public void ValidConfig_Loads()
    {
        var result = ConfigLoader.Load(WriteFile("valid.toml", ValidT));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Single(result.Config!.Device);
        Assert.Single(result.Config.Server);
        Assert.Equal(50200, result.Config.Server[0].Port);
        Assert.Equal(100, result.Config.Device[0].Bits);
    }

    [Fact]
    public void Defaults_Applied()
    {
        var result = ConfigLoader.Load(WriteFile("defaults.toml", ValidT));
        var server = result.Config!.Server[0];
        Assert.Equal("127.0.0.1", server.Bind);
        Assert.Null(server.UnitIds); // any unit id
        Assert.Equal(65536, result.Config.Device[0].Words);
    }

    [Fact]
    public void UnknownKey_Root_Fails()
    {
        var result = ConfigLoader.Load(WriteFile("bad-root.toml", ValidT + "\n\nservers = 3\n"));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("unknown key 'servers'"));
    }

    [Fact]
    public void UnknownKey_UnderServer_Fails()
    {
        var result = ConfigLoader.Load(WriteFile("bad-server.toml",
            ValidT.Replace("port = 50200", "port = 50200\nprotocl = \"modbus-tcp\"")));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("unknown key 'protocl'"));
    }

    [Fact]
    public void UnknownKey_UnderLog_Fails()
    {
        var result = ConfigLoader.Load(WriteFile("bad-log.toml",
            "[log]\nlevle = \"debug\"\n\n" + ValidT));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("unknown key 'levle'"));
    }

    [Fact]
    public void MultipleErrors_ReportedTogether()
    {
        var toml = """
            [[device]]
            name = "plc1"

            [[server]]
            name = "s1"
            protocol = "modbus-xxx"
            device = "missing"
            port = 99999

            [[server]]
            name = "s1"
            protocol = "modbus-tcp"
            device = "plc1"
            """;
        var result = ConfigLoader.Load(WriteFile("multi.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("modbus-xxx"));
        Assert.Contains(result.Errors, e => e.Message.Contains("device \"missing\" which is not declared"));
        Assert.Contains(result.Errors, e => e.Message.Contains("99999"));
        Assert.Contains(result.Errors, e => e.Message.Contains("duplicate server name"));
        Assert.True(result.Errors.Count >= 4, string.Join("\n", result.Errors));
    }

    [Fact]
    public void ParseError_Fails()
    {
        var result = ConfigLoader.Load(WriteFile("broken.toml", "[[device]\nname = \"x\n"));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("TOML parse error"));
    }

    [Fact]
    public void MissingFile_Fails()
    {
        var result = ConfigLoader.Load(Path.Combine(_dir, "nope.toml"));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("cannot read file"));
    }

    [Fact]
    public void UnitIds_RangeChecked()
    {
        var toml = ValidT.Replace("device = \"plc1\"", "device = \"plc1\"\nunit_ids = [1, 300]");
        var result = ConfigLoader.Load(WriteFile("units.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("unit_ids value 300"));
    }

    [Fact]
    public void DeviceSizes_RangeChecked()
    {
        var toml = ValidT.Replace("bits = 100", "bits = 0");
        var result = ConfigLoader.Load(WriteFile("sizes.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("bits value 0"));
    }

    [Fact]
    public void Validate_IsStandaloneReusable()
    {
        // the future `moz validate` reuses the same code path: Load == Validate
        var toml = ValidT.Replace("port = 50200", "port = -1");
        var result = ConfigLoader.Load(WriteFile("vrange.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("port value -1"));
    }
}