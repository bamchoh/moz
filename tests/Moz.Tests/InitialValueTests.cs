using Moz.Config;
using Moz.Devices;
using Tomlyn;
using Tomlyn.Model;

namespace Moz.Tests;

/// <summary>
/// [[variable]] value = ...: TOML values become host values per expanded
/// leaf (Resolve, shared by `moz validate` and startup) and are seeded into
/// the device without change notifications (Apply).
/// </summary>
public class InitialValueTests
{
    private static readonly IReadOnlyDictionary<string, StructDef> NoStructs = new Dictionary<string, StructDef>();

    private static readonly IReadOnlyDictionary<string, StructDef> Structs = new Dictionary<string, StructDef>
    {
        ["Motor"] = new StructDef("Motor",
        [
            new StructField("speed", "int"),
            new StructField("on", "bool"),
            new StructField("label", "string", Length: 6),
            new StructField("samples", "real", Count: 2),
        ]),
        ["Drive"] = new StructDef("Drive", [new StructField("motor", "Motor"), new StructField("id", "uint")]),
    };

    private static IReadOnlyList<Variable> Leaves(string type, int count = 1, int length = 0, TableKind table = TableKind.HoldingRegister, int offset = 0) =>
        VariableLayout.Expand("v", table, offset, type, count, length, Structs);

    private static object[] Single(string type, object value, int length = 0, TableKind table = TableKind.HoldingRegister)
    {
        var resolved = InitialValues.Resolve("v", 1, value, Leaves(type, 1, length, table));
        var (leaf, values) = Assert.Single(resolved);
        Assert.Equal("v", leaf.Name);
        return values;
    }

    private static TomlDateTime Utc(int y, int m, int d, int h = 0, int mi = 0, int s = 0) =>
        new(new DateTimeOffset(y, m, d, h, mi, s, TimeSpan.Zero), 0, TomlDateTimeKind.OffsetDateTimeByZ);

    // --- scalars --------------------------------------------------------------------

    [Theory]
    [InlineData("bool", true, true)]
    [InlineData("int", -5L, -5)]
    [InlineData("int", 32767L, 32767)]
    [InlineData("uint", 65535L, 65535u)]
    [InlineData("dint", -70000L, -70000)]
    [InlineData("udint", 4294967295L, 4294967295u)]
    [InlineData("real", 1.5, 1.5f)]
    [InlineData("real", 2L, 2f)]
    [InlineData("lint", -9000000000L, -9000000000L)]
    [InlineData("ulint", 9000000000L, 9000000000ul)]
    [InlineData("time", 1500L, 1500)]
    [InlineData("tod", 3600000L, 3600000u)]
    [InlineData("dt", 1793491200L, 1793491200ul)]
    public void Scalar_Converts_ToHostType(string type, object toml, object host)
    {
        var values = Single(type, toml, table: type == "bool" ? TableKind.Coil : TableKind.HoldingRegister);
        Assert.Equal(host, Assert.Single(values));
    }

    [Fact]
    public void String_Converts_WithinLength()
    {
        Assert.Equal("hello", Assert.Single(Single("string", "hello", length: 6)));
    }

    [Fact]
    public void DateTime_FromTomlDateTime_AndDateFloorsToMidnightUtc()
    {
        Assert.Equal(1793523600ul, Assert.Single(Single("dt", Utc(2026, 11, 1, 9, 0, 0))));   // 2026-11-01T09:00:00Z
        Assert.Equal(1793491200ul, Assert.Single(Single("date", Utc(2026, 11, 1, 9, 0, 0)))); // floored to 00:00Z
        Assert.Equal(1793491200ul, Assert.Single(Single("date", 1793523600L)));               // integer seconds floor too
        var local = new TomlDateTime(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), 0, TomlDateTimeKind.LocalDateTime);
        Assert.Equal(1793523600ul, Assert.Single(Single("dt", local)));                       // local date-time read as UTC
    }

    [Theory]
    [InlineData("bool", 1L, "expects true/false")]
    [InlineData("int", 32768L, "-32768..32767")]
    [InlineData("int", 1.5, "expects an integer")]
    [InlineData("uint", -1L, "0..65535")]
    [InlineData("udint", 4294967296L, "0..4294967295")]
    [InlineData("ulint", -1L, "0..")]
    [InlineData("real", "x", "expects a number")]
    [InlineData("time", 2147483648L, "-2147483648..2147483647")]
    [InlineData("tod", -1L, "0..4294967295")]
    [InlineData("dt", -1L, "0..")]
    [InlineData("dt", "2026-11-01", "expects a date-time")]
    [InlineData("string", 5L, "expects a string")]
    public void Scalar_WrongTypeOrRange_Throws(string type, object toml, string why)
    {
        var ex = Assert.Throws<FormatException>(() => Single(type, toml, length: type == "string" ? 4 : 0,
            table: type == "bool" ? TableKind.Coil : TableKind.HoldingRegister));
        Assert.Contains(why, ex.Message);
        Assert.StartsWith("value expects", ex.Message);
    }

    [Fact]
    public void String_TooLong_Throws()
    {
        var ex = Assert.Throws<FormatException>(() => Single("string", "toolong", length: 4));
        Assert.Contains("exceeds length 4", ex.Message);
    }

    [Fact]
    public void Scalar_GivenArrayOrTable_Throws()
    {
        Assert.Contains("expects a single value", Assert.Throws<FormatException>(() => Single("int", new TomlArray { 1L })).Message);
        Assert.Contains("expects a single value", Assert.Throws<FormatException>(() => Single("int", new TomlTable { ["a"] = 1L })).Message);
    }

    // --- arrays ---------------------------------------------------------------------

    [Fact]
    public void Array_RequiresExactlyCountElements()
    {
        var leaves = Leaves("int", count: 3);
        var (leaf, values) = Assert.Single(InitialValues.Resolve("v", 3, new TomlArray { 1L, 2L, 3L }, leaves));
        Assert.Equal(3, leaf.Count);
        Assert.Equal([1, 2, 3], values.Cast<int>().ToArray());

        var ex = Assert.Throws<FormatException>(() => InitialValues.Resolve("v", 3, new TomlArray { 1L, 2L }, leaves));
        Assert.Contains("expects an array of 3 elements", ex.Message);
        var ex2 = Assert.Throws<FormatException>(() => InitialValues.Resolve("v", 3, 1L, leaves));
        Assert.Contains("expects an array of 3 elements", ex2.Message);
        var ex3 = Assert.Throws<FormatException>(() => InitialValues.Resolve("v", 3, new TomlArray { 1L, "x", 3L }, leaves));
        Assert.StartsWith("value[1] expects an integer", ex3.Message);
    }

    // --- structs --------------------------------------------------------------------

    [Fact]
    public void Struct_TableFillsLeaves_MissingFieldsStayUnset()
    {
        var leaves = Leaves("Motor");
        var value = new TomlTable { ["speed"] = 7L, ["samples"] = new TomlArray { 1.0, 2.5 } };
        var resolved = InitialValues.Resolve("v", 1, value, leaves);
        Assert.Equal(["v.speed", "v.samples"], resolved.Select(r => r.Leaf.Name));
        Assert.Equal(7, Assert.Single(resolved[0].Values));
        Assert.Equal([1f, 2.5f], resolved[1].Values.Cast<float>().ToArray());
    }

    [Fact]
    public void Struct_NestedAndArrays()
    {
        var leaves = Leaves("Drive", count: 2);
        var value = new TomlArray
        {
            new TomlTable { ["motor"] = new TomlTable { ["on"] = true, ["label"] = "ab" }, ["id"] = 1L },
            new TomlTable { ["id"] = 2L },
        };
        var resolved = InitialValues.Resolve("v", 2, value, leaves);
        Assert.Equal(["v[0].motor.on", "v[0].motor.label", "v[0].id", "v[1].id"], resolved.Select(r => r.Leaf.Name));
        Assert.Equal(true, Assert.Single(resolved[0].Values));
        Assert.Equal(2u, Assert.Single(resolved[3].Values));
    }

    [Fact]
    public void Struct_Errors()
    {
        var leaves = Leaves("Motor");
        Assert.Contains("expects a table", Assert.Throws<FormatException>(() => InitialValues.Resolve("v", 1, 1L, leaves)).Message);
        Assert.Contains("unknown field \"value.rpm\"", Assert.Throws<FormatException>(() =>
            InitialValues.Resolve("v", 1, new TomlTable { ["rpm"] = 1L }, leaves)).Message);
        Assert.StartsWith("value.speed expects an integer", Assert.Throws<FormatException>(() =>
            InitialValues.Resolve("v", 1, new TomlTable { ["speed"] = "fast" }, leaves)).Message);
        Assert.Contains("value.samples expects an array of 2 elements", Assert.Throws<FormatException>(() =>
            InitialValues.Resolve("v", 1, new TomlTable { ["samples"] = 1.0 }, leaves)).Message);
        Assert.Contains("value.label exceeds length 6", Assert.Throws<FormatException>(() =>
            InitialValues.Resolve("v", 1, new TomlTable { ["label"] = "toolong!" }, leaves)).Message);

        var arr = Leaves("Motor", count: 2);
        Assert.Contains("expects an array of 2 tables", Assert.Throws<FormatException>(() =>
            InitialValues.Resolve("v", 2, new TomlTable { ["speed"] = 1L }, arr)).Message);
        Assert.Contains("value[1] expects a table", Assert.Throws<FormatException>(() =>
            InitialValues.Resolve("v", 2, new TomlArray { new TomlTable(), 1L }, arr)).Message);
    }

    // --- apply ------------------------------------------------------------------------

    [Fact]
    public void Apply_SeedsAnyTable_WithoutNotifying()
    {
        var device = new Device("d", coils: 16, discreteInputs: 16, holdingRegisters: 64, inputRegisters: 64);
        int notified = 0;
        using var w = device.Watch(TableKind.InputRegister, 0, 64, _ => notified++);
        using var w2 = device.Watch(TableKind.DiscreteInput, 0, 16, _ => notified++);

        static IReadOnlyList<Variable> Decl(string name, TableKind table, int offset, string type, int count = 1, int length = 0) =>
            VariableLayout.Expand(name, table, offset, type, count, length, Structs);
        InitialValues.Apply(device, InitialValues.Resolve("t", 1, 20.5, Decl("t", TableKind.InputRegister, 2, "real")));
        InitialValues.Apply(device, InitialValues.Resolve("s", 1, "hi", Decl("s", TableKind.InputRegister, 10, "string", length: 4)));
        InitialValues.Apply(device, InitialValues.Resolve("b", 3, new TomlArray { true, false, true }, Decl("b", TableKind.DiscreteInput, 1, "bool", count: 3)));
        InitialValues.Apply(device, InitialValues.Resolve("m", 1, new TomlTable { ["speed"] = -1L, ["on"] = true },
            Decl("m", TableKind.HoldingRegister, 20, "Motor")));

        var vars = new VariableTable(device,
        [
            new Variable("t", TableKind.InputRegister, 2, VariableType.Real),
            new Variable("s", TableKind.InputRegister, 10, VariableType.String, Length: 4),
            new Variable("b", TableKind.DiscreteInput, 1, VariableType.Bool, Count: 3),
            new Variable("m.speed", TableKind.HoldingRegister, 20, VariableType.Int),
            new Variable("m.on", TableKind.HoldingRegister, 21, VariableType.BoolWord),
        ], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(20.5f, vars.Read<float>("t"));
        Assert.Equal("hi", vars.Read<string>("s"));
        Assert.Equal([true, false, true], vars.ReadAll<bool>("b"));
        Assert.Equal(-1, vars.Read<int>("m.speed"));
        Assert.True(vars.Read<bool>("m.on"));
        Assert.Equal(0, notified);
    }
}
