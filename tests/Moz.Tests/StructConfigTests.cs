namespace Moz.Tests;

/// <summary>
/// [[struct]] config (phase 8): user-defined struct types whose fields are
/// laid out sequentially on a word table. A [[variable]] with a struct type
/// expands at load time into dotted leaf variables (motor.speed); the
/// VariableTable itself stays flat.
/// </summary>
public class StructConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-structtests-" + Guid.NewGuid().ToString("N"));

    public StructConfigTests() => Directory.CreateDirectory(_dir);

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

    private const string TwoStructs =
        """
        [[device]]
        name = "plc1"
        words = 100

        [[struct]]
        name = "Motor"

        [[struct.field]]
        name = "speed"
        type = "int"

        [[struct.field]]
        name = "on"
        type = "bool"

        [[struct.field]]
        name = "rpm"
        type = "real"

        [[struct]]
        name = "Pump"

        [[struct.field]]
        name = "flow"
        type = "real"

        [[struct.field]]
        name = "label"
        type = "string"
        length = 6

        [[struct.field]]
        name = "samples"
        type = "int"
        count = 4
        """;

    /// <summary>The Tomlyn spike: two nested table arrays with 3 elements each must bind.</summary>
    [Fact]
    public void TwoStructs_ThreeFieldsEach_Bind()
    {
        var result = Load("two.toml", TwoStructs);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var structs = result.Config!.Struct;
        Assert.Equal(2, structs.Count);
        Assert.Equal("Motor", structs[0].Name);
        Assert.Equal(["speed", "on", "rpm"], structs[0].Field.Select(f => f.Name));
        Assert.Equal(["int", "bool", "real"], structs[0].Field.Select(f => f.Type));
        Assert.Equal("Pump", structs[1].Name);
        Assert.Equal(["flow", "label", "samples"], structs[1].Field.Select(f => f.Name));
        Assert.Equal(6, structs[1].Field[1].Length);
        Assert.Equal(4, structs[1].Field[2].Count);
        Assert.Equal(1, structs[1].Field[0].Count); // default
    }

    // --- validation ------------------------------------------------------------------

    private const string MotorVar =
        "\n[[variable]]\nname = \"motor\"\ndevice = \"plc1\"\nkind = \"words\"\noffset = 0\ntype = \"Motor\"\n";

    [Fact]
    public void StructVariable_Loads_And_ToStructDefs()
    {
        var result = Load("ok.toml", TwoStructs + MotorVar);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var defs = Moz.Config.ConfigLoader.ToStructDefs(result.Config!);
        Assert.Equal(2, defs.Count);
        Assert.Equal(["speed", "on", "rpm"], defs["Motor"].Fields.Select(f => f.Name));
        Assert.Equal(6, defs["Pump"].Fields[1].Length);
        Assert.Equal(4, defs["Pump"].Fields[2].Count);
    }

    [Fact]
    public void UnknownVariableType_ListsStructs()
    {
        var result = Load("unknown.toml", TwoStructs + MotorVar.Replace("type = \"Motor\"", "type = \"Motr\""));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("variable.type value \"Motr\" is invalid") && e.Message.Contains("Motor|Pump"));
    }

    [Fact]
    public void UnknownFieldType_Fails()
    {
        var toml = TwoStructs.Replace("name = \"rpm\"\ntype = \"real\"", "name = \"rpm\"\ntype = \"reel\"");
        var result = Load("fieldtype.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("struct.field.type value \"reel\" is invalid") && e.Message.Contains("[[struct]] \"Motor\" field \"rpm\""));
    }

    [Fact]
    public void Cycle_Fails()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[struct]]
            name = "A"
            [[struct.field]]
            name = "b"
            type = "B"

            [[struct]]
            name = "B"
            [[struct.field]]
            name = "a"
            type = "A"
            """;
        var result = Load("cycle.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("struct cycle"));
    }

    [Fact]
    public void DuplicateStructName_Fails()
    {
        var result = Load("dupstruct.toml", TwoStructs.Replace("name = \"Pump\"", "name = \"Motor\""));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("duplicate struct name \"Motor\""));
    }

    [Fact]
    public void DuplicateFieldName_Fails()
    {
        var result = Load("dupfield.toml", TwoStructs.Replace("name = \"on\"", "name = \"speed\""));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("duplicate field name \"speed\" in [[struct]] \"Motor\""));
    }

    [Fact]
    public void StructNameCollidesWithScalar_Fails()
    {
        var result = Load("scalarname.toml", TwoStructs.Replace("name = \"Pump\"", "name = \"int\""));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("struct name \"int\" collides with a built-in type"));
    }

    [Fact]
    public void EmptyStruct_And_MissingNames_Fail()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[struct]]
            name = "Empty"

            [[struct]]
            name = ""
            [[struct.field]]
            name = ""
            type = "int"
            """;
        var result = Load("empty.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("[[struct]] \"Empty\" has no fields"));
        Assert.Contains(result.Errors, e => e.Message.Contains("struct.name is required"));
        Assert.Contains(result.Errors, e => e.Message.Contains("struct.field.name is required"));
    }

    [Fact]
    public void UnknownKeys_InStructAndField_Fail()
    {
        var toml = TwoStructs.Replace("name = \"Motor\"\n", "name = \"Motor\"\ncolor = \"red\"\n")
                             .Replace("name = \"speed\"\ntype = \"int\"", "name = \"speed\"\ntype = \"int\"\nunit = \"rpm\"");
        var result = Load("extra.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("unknown key 'color'"));
        Assert.Contains(result.Errors, e => e.Message.Contains("unknown key 'unit'"));
    }

    [Fact]
    public void StringField_LengthRules()
    {
        var noLength = TwoStructs.Replace("type = \"string\"\nlength = 6", "type = \"string\"");
        var result = Load("fieldnolen.toml", noLength);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("struct.field.length is required for type \"string\""));

        var lengthOnInt = TwoStructs.Replace("name = \"speed\"\ntype = \"int\"", "name = \"speed\"\ntype = \"int\"\nlength = 2");
        result = Load("fieldlenint.toml", lengthOnInt);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("struct.field.length is only valid for type \"string\""));

        var tooLong = TwoStructs.Replace("length = 6", "length = 251");
        result = Load("fieldlen251.toml", tooLong);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("struct.field.length value 251 is out of range 1..250"));

        var zeroCount = TwoStructs.Replace("count = 4", "count = 0");
        result = Load("fieldcount0.toml", zeroCount);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("struct.field.count value 0 is out of range 1.."));
    }

    [Fact]
    public void StructVariable_OnBitTable_Fails()
    {
        var result = Load("coil.toml", TwoStructs + MotorVar.Replace("kind = \"words\"", "kind = \"bits\""));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("type \"Motor\" requires kind words|read_only_words"));
    }

    [Fact]
    public void StructVariable_ExceedsDevice_UsesExpandedCells()
    {
        // Motor = 4 words; holding = 100 → offset 97 is over by one
        var result = Load("oor.toml", TwoStructs + MotorVar.Replace("offset = 0", "offset = 97"));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("offset 97 (+4 cells)"));
    }

    [Fact]
    public void StructArray_ExceedsDevice_UsesStride()
    {
        // 2 × Motor = 8 words; offset 93 → over by one
        var result = Load("arroor.toml", TwoStructs + MotorVar.Replace("offset = 0", "offset = 93\ncount = 2"));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("offset 93 (+8 cells)"));
    }

    [Fact]
    public void StructVariable_Overlap_UsesExpandedCells()
    {
        var other = "\n[[variable]]\nname = \"x\"\ndevice = \"plc1\"\nkind = \"words\"\noffset = 3\ntype = \"int\"\n";
        var result = Load("overlap.toml", TwoStructs + MotorVar + other);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("overlaps \"motor\" (offset 0, 4 cells)"));
    }

    [Fact]
    public void ExpandedLeafName_Collision_Fails()
    {
        var other = "\n[[variable]]\nname = \"motor.speed\"\ndevice = \"plc1\"\nkind = \"words\"\noffset = 50\ntype = \"int\"\n";
        var result = Load("leafdup.toml", TwoStructs + MotorVar + other);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("duplicate variable name \"motor.speed\""));
    }

    [Fact]
    public void StructVariable_WithLength_Fails()
    {
        var result = Load("structlen.toml", TwoStructs + MotorVar + "length = 4\n");
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("variable.length is only valid for type \"string\" (got type \"Motor\")"));
    }
}
