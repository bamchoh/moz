using Moz.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moz.Tests;

/// <summary>
/// Typed named variables: the overlay that gives raw memory cells names and
/// IEC-ish types. Scripts (phase 4) and the OPC UA server / GUI consume this.
/// </summary>
public class VariableTests
{
    private readonly Device _device = new("plc-a", coils: 100, discreteInputs: 100, holdingRegisters: 100, inputRegisters: 100);

    private static Variable V(string name, TableKind table, int offset, VariableType type, int count = 1) =>
        new(name, table, offset, type, count);

    // --- cell sizes / ctor validation --------------------------------------

    [Fact]
    public void WordSizes_Understood()
    {
        Assert.Equal(1, Variable.WordSize(VariableType.Int));
        Assert.Equal(1, Variable.WordSize(VariableType.Uint));
        Assert.Equal(2, Variable.WordSize(VariableType.Dint));
        Assert.Equal(2, Variable.WordSize(VariableType.Udint));
        Assert.Equal(2, Variable.WordSize(VariableType.Real));
    }

    [Fact]
    public void Bool_IsBitBounded_WordTypesNeedWordTables()
    {
        // bool on a word table → ctor rejects (config validation reports it earlier)
        Assert.Throws<ArgumentException>(() => new VariableTable(_device,
            [V("b", TableKind.HoldingRegister, 0, VariableType.Bool)], NullLogger.Instance));

        // word type on a bit table → ctor rejects
        Assert.Throws<ArgumentException>(() => new VariableTable(_device,
            [V("i", TableKind.Coil, 0, VariableType.Int)], NullLogger.Instance));
    }

    [Fact]
    public void OffsetBeyondTable_Rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VariableTable(_device,
            [V("big", TableKind.HoldingRegister, 99, VariableType.Dint)], NullLogger.Instance)); // needs 2 words, only 1 left
        new VariableTable(_device, [V("ok", TableKind.HoldingRegister, 98, VariableType.Dint)],
            NullLogger.Instance); // fits exactly
    }

    // --- typed read/write ---------------------------------------------------

    [Fact]
    public void Real_RoundTrip_BigEndianTwoWords()
    {
        var table = new VariableTable(_device, [V("temperature", TableKind.HoldingRegister, 10, VariableType.Real)],
            NullLogger.Instance);
        table.Write("temperature", 12.5f);

        // IEEE754 12.5 = 0x4148_0000 → words 0x4148, 0x0000 (big-endian)
        var w = new ushort[2];
        _device.Read(TableKind.HoldingRegister, 10, w);
        Assert.Equal((ushort)0x4148, w[0]);
        Assert.Equal((ushort)0x0000, w[1]);

        Assert.Equal(12.5f, table.Read<float>("temperature"));
    }

    [Fact]
    public void Int_And_Uint_RoundTrip_ThroughRawWords()
    {
        var table = new VariableTable(_device,
        [
            V("pressure", TableKind.HoldingRegister, 0, VariableType.Int),
            V("rpm", TableKind.HoldingRegister, 1, VariableType.Uint),
        ], NullLogger.Instance);

        table.Write("pressure", -5);
        table.Write("rpm", 65000u);

        var w = new ushort[2];
        _device.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal(0xFFFB, w[0]); // -5 as 16-bit two's complement
        Assert.Equal(65000, w[1]);

        Assert.Equal(-5, table.Read<int>("pressure"));
        Assert.Equal(65000u, table.Read<uint>("rpm"));
    }

    [Fact]
    public void Dint_And_Udint_TwoWords_BigEndianHighFirst()
    {
        var table = new VariableTable(_device,
        [
            V("total", TableKind.HoldingRegister, 4, VariableType.Dint),
            V("serial", TableKind.HoldingRegister, 6, VariableType.Udint),
        ], NullLogger.Instance);

        table.Write("total", 0x12345678);
        table.Write("serial", 0xFFFFFFFFu);

        var w = new ushort[4];
        _device.Read(TableKind.HoldingRegister, 4, w);
        Assert.Equal((ushort)0x1234, w[0]); // high word first
        Assert.Equal((ushort)0x5678, w[1]);
        Assert.Equal((ushort)0xFFFF, w[2]);
        Assert.Equal((ushort)0xFFFF, w[3]);

        Assert.Equal(0x12345678, table.Read<int>("total"));
        Assert.Equal(0xFFFFFFFFu, table.Read<uint>("serial"));
    }

    [Fact]
    public void Dint_NegativeValue_SignExtendsCorrectly()
    {
        var table = new VariableTable(_device, [V("temp", TableKind.HoldingRegister, 0, VariableType.Dint)],
            NullLogger.Instance);
        table.Write("temp", -100000);
        Assert.Equal(-100000, table.Read<int>("temp"));
    }

    [Fact]
    public void Bool_OnCoil_ReadWrite()
    {
        var table = new VariableTable(_device, [V("pumpOn", TableKind.Coil, 7, VariableType.Bool)],
            NullLogger.Instance);
        table.Write("pumpOn", true);
        Assert.True(_device.ReadBit(TableKind.Coil, 7));
        Assert.True(table.Read<bool>("pumpOn"));
        table.Write("pumpOn", false);
        Assert.False(table.Read<bool>("pumpOn"));
    }

    [Fact]
    public void Bool_FromDiscrete_ReadOnly()
    {
        _device.Seed(TableKind.DiscreteInput, 3, new bool[] { true });
        var table = new VariableTable(_device, [V("doorOpen", TableKind.DiscreteInput, 3, VariableType.Bool)],
            NullLogger.Instance);
        Assert.True(table.Read<bool>("doorOpen"));
        Assert.ThrowsAny<InvalidOperationException>(() => table.Write<bool>("doorOpen", false));
    }

    // --- arrays ---------------------------------------------------------------

    [Fact]
    public void Arrays_ElementsAddressContiguousCells()
    {
        var table = new VariableTable(_device, [V("samples", TableKind.HoldingRegister, 20, VariableType.Int, 4)],
            NullLogger.Instance);

        table.Write("samples", 1, index: 0);
        table.Write("samples", 42, index: 3);

        var w = new ushort[4];
        _device.Read(TableKind.HoldingRegister, 20, w);
        Assert.Equal(1, w[0]);
        Assert.Equal(42, w[3]);
    }

    [Fact]
    public void Array_BulkReadWrite()
    {
        var table = new VariableTable(_device, [V("samples", TableKind.HoldingRegister, 20, VariableType.Uint, 3)],
            NullLogger.Instance);

        table.WriteAll("samples", new uint[] { 10, 20, 30 });
        Assert.Equal([10u, 20u, 30u], table.ReadAll<uint>("samples"));
    }

    [Fact]
    public void Array_IndexOutOfRange_Throws()
    {
        var table = new VariableTable(_device, [V("samples", TableKind.HoldingRegister, 20, VariableType.Int, 4)],
            NullLogger.Instance);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Read<int>("samples", 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Write("samples", 1, index: -1));
    }

    // --- lookup ---------------------------------------------------------------

    [Fact]
    public void UnknownOrDuplicateName_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => new VariableTable(_device,
            [V("a", TableKind.HoldingRegister, 0, VariableType.Int), V("a", TableKind.HoldingRegister, 1, VariableType.Int)],
            NullLogger.Instance));
        var table = new VariableTable(_device, [V("a", TableKind.HoldingRegister, 0, VariableType.Int)],
            NullLogger.Instance);
        Assert.Throws<KeyNotFoundException>(() => table.Read<int>("nope"));
    }

    // --- change logging -------------------------------------------------------

    [Fact]
    public async Task DeviceWrite_LogsVariableChange()
    {
        var provider = new CapturingProvider();
        var table = new VariableTable(_device, [V("temperature", TableKind.HoldingRegister, 10, VariableType.Real)],
            provider.CreateLogger("test"));

        table.Write("temperature", 12.5f); // own write should also be logged (GUI/OPC UA parity)
        var req = provider.Entries.FirstOrDefault(e => e.EventId == 1101);
        Assert.True(req is not null,
            $"entries: {string.Join(" | ", provider.Entries.Select(e => $"{e.EventId}|[{e.Message}]"))}");
        Assert.Equal("plc-a temperature=12.5", req.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ModbusMasterWrite_LogsCoveredVariable_OncePerBatch()
    {
        // simulate what happens when the Modbus transport writes raw cells:
        // the variable-level watcher notices and logs ONE line per write batch,
        // formatting the whole array (values re-read from the device).
        var provider = new CapturingProvider();
        var table = new VariableTable(_device,
        [
            V("temperature", TableKind.HoldingRegister, 10, VariableType.Real, 2),
        ], provider.CreateLogger("test"));

        Span<ushort> raw = [0x4148, 0x0000, 0x4148, 0x0000]; // 12.5, 12.5
        _device.Write(TableKind.HoldingRegister, 10, raw);
        await Task.Delay(100);

        var changes = provider.Entries.Where(e => e.EventId == 1101).ToList();
        Assert.Single(changes);
        Assert.Equal("plc-a temperature=[12.5,12.5]", changes[0].Message);
        await Task.CompletedTask;
    }

    [Fact]
    public void Bool_ArrayFormatting()
    {
        var provider = new CapturingProvider();
        var table = new VariableTable(_device, [V("flags", TableKind.Coil, 0, VariableType.Bool, 3)],
            provider.CreateLogger("test"));
        Span<bool> bits = [true, false, true];
        _device.Write(TableKind.Coil, 0, bits);
        var req = provider.Entries.FirstOrDefault(e => e.EventId == 1101)?.Message;
        Assert.Equal("plc-a flags=[true,false,true]", req);
    }
}