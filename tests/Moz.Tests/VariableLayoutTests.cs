using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.OpcUa;
using Opc.Ua;

namespace Moz.Tests;

/// <summary>
/// Struct expansion (phase 8): a struct-typed declaration becomes dotted leaf
/// variables laid out sequentially on a word table. bool fields take one
/// word (BoolWord: 0 = false, non-zero = true). Indices are 0-based to match
/// readVariable(name, index).
/// </summary>
public class VariableLayoutTests
{
    private static readonly IReadOnlyDictionary<string, StructDef> Structs = new Dictionary<string, StructDef>
    {
        ["Motor"] = new("Motor",
        [
            new StructField("speed", "int"),
            new StructField("on", "bool"),
            new StructField("rpm", "real"),
        ]),
        ["Drive"] = new("Drive",
        [
            new StructField("motor", "Motor"),
            new StructField("temp", "real"),
        ]),
        ["Tagged"] = new("Tagged",
        [
            new StructField("label", "string", Length: 5),
            new StructField("samples", "int", Count: 4),
            new StructField("motors", "Motor", Count: 2),
        ]),
    };

    private static IReadOnlyList<Variable> Expand(string name, string type, int offset = 0, int count = 1, int length = 0) =>
        VariableLayout.Expand(name, TableKind.HoldingRegister, offset, type, count, length, Structs);

    [Fact]
    public void Scalar_PassesThrough()
    {
        var leaves = Expand("t", "real", offset: 3, count: 2);
        var leaf = Assert.Single(leaves);
        Assert.Equal(new Variable("t", TableKind.HoldingRegister, 3, VariableType.Real, 2), leaf);

        var s = Assert.Single(Expand("label", "string", length: 7));
        Assert.Equal(VariableType.String, s.Type);
        Assert.Equal(7, s.Length);
    }

    [Fact]
    public void Struct_LaysOutFieldsSequentially_BoolIsOneWord()
    {
        var leaves = Expand("motor", "Motor", offset: 10);
        Assert.Equal(
        [
            new Variable("motor.speed", TableKind.HoldingRegister, 10, VariableType.Int),
            new Variable("motor.on", TableKind.HoldingRegister, 11, VariableType.BoolWord),
            new Variable("motor.rpm", TableKind.HoldingRegister, 12, VariableType.Real),
        ], leaves);
        Assert.Equal(4, VariableLayout.Words("Motor", 0, Structs));
    }

    [Fact]
    public void Nested_Struct_PathNames()
    {
        var leaves = Expand("drive", "Drive", offset: 0);
        Assert.Equal(["drive.motor.speed", "drive.motor.on", "drive.motor.rpm", "drive.temp"],
            leaves.Select(l => l.Name));
        Assert.Equal([0, 1, 2, 4], leaves.Select(l => l.Offset));
        Assert.Equal(6, VariableLayout.Words("Drive", 0, Structs));
    }

    [Fact]
    public void StructArray_OneLeafSetPerElement_ZeroBased()
    {
        var leaves = Expand("motors", "Motor", offset: 20, count: 2);
        Assert.Equal(["motors[0].speed", "motors[0].on", "motors[0].rpm", "motors[1].speed", "motors[1].on", "motors[1].rpm"],
            leaves.Select(l => l.Name));
        Assert.Equal([20, 21, 22, 24, 25, 26], leaves.Select(l => l.Offset));
    }

    [Fact]
    public void FieldArray_StringField_NestedArray()
    {
        var leaves = Expand("t", "Tagged", offset: 0);
        Assert.Equal(["t.label", "t.samples", "t.motors[0].speed", "t.motors[0].on", "t.motors[0].rpm",
                      "t.motors[1].speed", "t.motors[1].on", "t.motors[1].rpm"],
            leaves.Select(l => l.Name));
        Assert.Equal(VariableType.String, leaves[0].Type);
        Assert.Equal(5, leaves[0].Length);
        Assert.Equal(3, leaves[0].Cells);
        Assert.Equal(4, leaves[1].Count); // field array stays one leaf with count
        Assert.Equal(3, leaves[1].Offset);
        Assert.Equal(7, leaves[2].Offset);
        Assert.Equal(11, leaves[5].Offset);
        Assert.Equal(15, VariableLayout.Words("Tagged", 0, Structs));
    }

    [Fact]
    public void UnknownType_And_Cycle_Throw()
    {
        Assert.Throws<ArgumentException>(() => Expand("x", "Nope"));
        var cyclic = new Dictionary<string, StructDef>
        {
            ["A"] = new("A", [new StructField("b", "B")]),
            ["B"] = new("B", [new StructField("a", "A")]),
        };
        Assert.Throws<ArgumentException>(() => VariableLayout.Words("A", 0, cyclic));
        Assert.Throws<ArgumentException>(() =>
            VariableLayout.Expand("a", TableKind.HoldingRegister, 0, "A", 1, 0, cyclic));
    }

    // --- BoolWord at the three touchpoints ----------------------------------------

    [Fact]
    public void BoolWord_RoundTrip_NonZeroIsTrue_LogsLowercase()
    {
        var device = new Device("plc-a", holdingRegisters: 10);
        var provider = new CapturingProvider();
        var table = new VariableTable(device,
            [new Variable("on", TableKind.HoldingRegister, 0, VariableType.BoolWord)], provider.CreateLogger("t"));

        device.Write(TableKind.HoldingRegister, 0, new ushort[] { 0x0005 });
        Assert.True(table.Read<bool>("on"));
        Assert.Contains(provider.Entries, e => e.Message == "plc-a on=true");

        table.Write("on", false);
        var w = new ushort[1];
        device.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal((ushort)0, w[0]);
        table.Write("on", true);
        device.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal((ushort)1, w[0]);
        Assert.Throws<ArgumentException>(() => table.Read<int>("on"));
    }

    [Fact]
    public void BoolWord_UaCodec_IsBoolean()
    {
        var device = new Device("plc-a", holdingRegisters: 10);
        var v = new Variable("on", TableKind.HoldingRegister, 0, VariableType.BoolWord);
        var table = new VariableTable(device, [v], NullLogger.Instance);

        Assert.Equal(DataTypeIds.Boolean, UaValueCodec.DataType(VariableType.BoolWord));
        UaValueCodec.Write(table, v, new Variant(true));
        Assert.True(table.Read<bool>("on"));
        Assert.True(UaValueCodec.Read(table, v).TryGetValue(out bool b) && b);
        Assert.Throws<FormatException>(() => UaValueCodec.Write(table, v, new Variant((short)1)));
    }

    [Fact]
    public void StructLeaf_ChangeLog_OneLinePerLeaf()
    {
        var device = new Device("plc-a", holdingRegisters: 10);
        var provider = new CapturingProvider();
        var table = new VariableTable(device,
            VariableLayout.Expand("motor", TableKind.HoldingRegister, 0, "Motor", 1, 0, Structs), provider.CreateLogger("t"));

        // a Modbus master writes the whole struct in one FC16 → one line per leaf
        device.Write(TableKind.HoldingRegister, 0, new ushort[] { 0x0007, 0x0001, 0x4148, 0x0000 });

        var lines = provider.Entries.Where(e => e.EventId == 1101).Select(e => e.Message).ToList();
        Assert.Equal(["plc-a motor.speed=7", "plc-a motor.on=true", "plc-a motor.rpm=12.5"], lines);
        Assert.Equal(12.5f, table.Read<float>("motor.rpm"));
    }
}
