namespace Moz.Tests;

/// <summary>Config-level tests for [[script]] (phase 4).</summary>
public class ScriptConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-scfg-" + Guid.NewGuid().ToString("N"));

    public ScriptConfigTests() => Directory.CreateDirectory(_dir);

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

    private const string Base =
        """
        [[device]]
        name = "plc1"

        [[server]]
        name = "sim"
        protocol = "modbus-tcp"
        device = "plc1"

        [[script]]
        name = "counter"
        device = "plc1"
        path = "scripts/counter.js"
        interval_ms = 500
        """;

    [Fact]
    public void ValidScript_Loads()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("ok.toml", Base));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var s = result.Config!.Script;
        Assert.Single(s);
        Assert.Equal("counter", s[0].Name);
        Assert.Equal("scripts/counter.js", s[0].Path);
        Assert.Equal(500, s[0].IntervalMs);
    }

    [Fact]
    public void MissingDevice_Fails()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[script]]
            name = "c1"
            path = "s.js"
            """;
        var result = Moz.Config.ConfigLoader.Load(WriteFile("nodev.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("script.device is required"));
    }

    [Fact]
    public void UnknownScriptDevice_Fails()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[script]]
            name = "c1"
            device = "nope"
            path = "s.js"
            """;
        var result = Moz.Config.ConfigLoader.Load(WriteFile("ref.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("references device \"nope\""));
    }

    [Fact]
    public void MissingNameOrPath_Fails()
    {
        var toml = Base.Replace("name = \"counter\"", "");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("noname.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("script.name is required"));

        var toml2 = Base.Replace("path = \"scripts/counter.js\"", "");
        var result2 = Moz.Config.ConfigLoader.Load(WriteFile("nopath.toml", toml2));
        Assert.False(result2.Ok);
        Assert.Contains(result2.Errors, e => e.Message.Contains("script.path is required"));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(3600001)]
    public void IntervalMs_BoundsChecked(int interval)
    {
        var toml = Base.Replace("interval_ms = 500", $"interval_ms = {interval}");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("bad-int.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains($"interval_ms value {interval}"));
    }

    /// <summary>Base with normalized line endings (the raw literal must not
    /// depend on the file's CRLF/LF state when tests patch it by string ops).</summary>
    private static string Norm(string s) => s.Replace("\r\n", "\n");
    private string BaseN => Norm(Base);

    [Fact]
    public void DuplicateScriptName_Fails()
    {
        var toml = BaseN.Replace("[[script]]\nname = \"counter\"",
            "[[script]]\nname = \"counter\"\ndevice = \"plc1\"\npath = \"b.js\"\ninterval_ms = 600\n\n[[script]]\nname = \"counter\"");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("dupe.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("duplicate script name"));
    }

    [Fact]
    public void TypoKey_Fails()
    {
        var toml = Base.Replace("interval_ms = 500", "interval_msX = 500");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("typo.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("unknown key 'interval_msX'"));
    }

    [Fact]
    public void DefaultInterval_Is1000()
    {
        var toml = Base.Replace("interval_ms = 500", "");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("def.toml", toml));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(1000, result.Config!.Script[0].IntervalMs);
    }
}