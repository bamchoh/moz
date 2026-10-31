namespace Moz.Tests;

/// <summary>
/// Config-level tests for [[variable]]: strictness, ranges, kind/type
/// compatibility and overlap detection. Same loader / same error style as all
/// other config keys. Top-level shape (not nested in [[device]]) because
/// Tomlyn 2.x cannot bind nested table arrays.
/// </summary>
public class VariableConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-vartests-" + Guid.NewGuid().ToString("N"));

    public VariableConfigTests() => Directory.CreateDirectory(_dir);

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
        holding = 100
        coils = 100

        [[variable]]
        name = "temperature"
        device = "plc1"
        kind = "holding"
        offset = 0
        type = "real"

        [[server]]
        name = "sim"
        protocol = "modbus-tcp"
        device = "plc1"
        port = 50200
        """;

    private static string AppendVariable(string name, string kind, string offset, string type, string? count = null) =>
        Base.Replace("\n[[server]]",
            $"\n[[variable]]\nname = \"{name}\"\ndevice = \"plc1\"\nkind = \"{kind}\"\noffset = {offset}\ntype = \"{type}\""
            + (count is null ? "" : $"\ncount = {count}")
            + "\n\n[[server]]");

    [Fact]
    public void ValidVariables_Load()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("ok.toml", Base));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var v = result.Config!.Variable;
        Assert.Single(v);
        Assert.Equal("temperature", v[0].Name);
        Assert.Equal("plc1", v[0].Device);
        Assert.Equal(1, v[0].Count); // default
    }

    [Fact]
    public void TypoKey_Fails()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("typo.toml", Base.Replace("offset = 0", "ofset = 0")));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("unknown key 'ofset'"));
    }

    [Theory]
    [InlineData("kind", "registers")]
    [InlineData("type", "float32")]
    public void BadEnum_Fails(string key, string value)
    {
        var toml = Base.Replace("kind = \"holding\"", $"kind = \"{(key == "kind" ? value : "holding")}\"")
                        .Replace("type = \"real\"", $"type = \"{(key == "type" ? value : "real")}\"");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("enum.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains($"variable.{key} value \"{value}\""));
    }

    [Fact]
    public void Bool_OnWordTable_Fails()
    {
        var toml = Base.Replace("type = \"real\"", "type = \"bool\"");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("boolword.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("type \"bool\" requires kind coil|discrete"));
    }

    [Fact]
    public void Int_OnBitTable_Fails()
    {
        var toml = Base.Replace("kind = \"holding\"", "kind = \"coil\"").Replace("type = \"real\"", "type = \"int\"");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("intbit.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("type \"int\" requires kind holding|input"));
    }

    [Fact]
    public void OffsetPlusSizeBeyondDevice_Fails()
    {
        // holding = 100 words; a dint at offset 99 needs 2 → over by exactly one
        var toml = Base.Replace("offset = 0", "offset = 99").Replace("type = \"real\"", "type = \"dint\"");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("oor.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("offset 99"));
    }

    [Fact]
    public void UnknownDeviceReference_Fails()
    {
        var toml = Base.Replace("device = \"plc1\"\nkind = \"holding\"", "device = \"nope\"\nkind = \"holding\"");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("ref.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("references device \"nope\" which is not declared"));
    }

    [Fact]
    public void DuplicateName_PerDevice_Fails()
    {
        var dupe = AppendVariable("temperature", "holding", "10", "real");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("dupe.toml", dupe));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("duplicate variable name \"temperature\""));
    }

    private static readonly string TwoDevices =
        """
        [[device]]
        name = "plc1"
        holding = 100

        [[device]]
        name = "plc2"

        [[variable]]
        name = "temperature"
        device = "plc1"
        kind = "holding"
        offset = 0
        type = "real"

        [[variable]]
        name = "temperature"
        device = "plc2"
        kind = "holding"
        offset = 1
        type = "real"

        [[server]]
        name = "sim"
        protocol = "modbus-tcp"
        device = "plc1"
        port = 50200
        """;

    [Fact]
    public void SameNameOnDifferentDevice_IsOK()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("twodev.toml", TwoDevices));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal(2, result.Config!.Variable.Count);
    }

    [Fact]
    public void OverlappingRange_Fails()
    {
        // real at 0 occupies words 0-1; other at 1 overlaps
        var overlap = AppendVariable("other", "holding", "1", "dint");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("overlap.toml", overlap));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("overlaps"));
    }

    [Fact]
    public void AdjoiningNonOverlapping_IsOK()
    {
        // real at 0 covers words 0-1; another int at 2 does not overlap
        var ok = AppendVariable("rpm", "holding", "2", "int");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("adjoin.toml", ok));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Fact]
    public void OverlapBetweenVariablesOfDifferentDevices_IsOK()
    {
        // the same offset/kind on two different devices never overlaps
        var result = Moz.Config.ConfigLoader.Load(WriteFile("twodev2.toml", TwoDevices));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
    }

    [Fact]
    public void ArrayCount_RangeChecked()
    {
        var big = AppendVariable("big", "holding", "99", "int", "5");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("bigcount.toml", big));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("offset 99"));
    }

    [Fact]
    public void ArrayCount_Zero_Fails()
    {
        var zero = Base.Replace("type = \"real\"", "type = \"real\"\ncount = 0");
        var result = Moz.Config.ConfigLoader.Load(WriteFile("zerocount.toml", zero));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("variable.count value 0"));
    }
}