using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;

namespace Moz.Tests;

/// <summary>
/// STRING and time types (phase 8). Register layouts are karakuri-compatible:
/// string = ceil(length/2) words, first character in the high byte, UTF-8,
/// NUL padded; time = int32 ms (2 words), tod = uint32 ms (2 words),
/// date / dt = uint64 seconds (4 words). The host types are string / int /
/// uint / ulong — VariableTable does no semantic conversion.
/// </summary>
public class StringTimeTests
{
    private readonly Device _device = new("plc-a", words: 100);

    private static Variable Str(string name, int offset, int length, int count = 1) =>
        new(name, TableKind.Words, offset, VariableType.String, count, length);

    private VariableTable Table(params Variable[] vars) => new(_device, vars, NullLogger.Instance);

    private ushort[] Words(int offset, int count)
    {
        var w = new ushort[count];
        _device.Read(TableKind.Words, offset, w);
        return w;
    }

    // --- sizes -------------------------------------------------------------------

    [Fact]
    public void String_OccupiesCeilHalfLengthWords()
    {
        Assert.Equal(3, Str("s", 0, 5).Cells);
        Assert.Equal(2, Str("s", 0, 4).Cells);
        Assert.Equal(1, Str("s", 0, 1).Cells);
        Assert.Equal(3, Str("s", 0, 5).ElementWords);
        Assert.Equal(6, Str("s", 0, 5, count: 2).Cells);
        Assert.Throws<ArgumentException>(() => Variable.WordSize(VariableType.String));
    }

    [Fact]
    public void TimeTypes_WordSizes()
    {
        Assert.Equal(2, Variable.WordSize(VariableType.Time));
        Assert.Equal(2, Variable.WordSize(VariableType.Tod));
        Assert.Equal(4, Variable.WordSize(VariableType.Date));
        Assert.Equal(4, Variable.WordSize(VariableType.Dt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(251)]
    public void String_LengthOutOfRange_Throws(int length)
    {
        Assert.ThrowsAny<ArgumentException>(() => Table(Str("s", 0, length)));
    }

    // --- string layout ---------------------------------------------------------------

    [Fact]
    public void String_RoundTrip_HighByteFirst_NulPadded()
    {
        var table = Table(Str("label", 0, 5));
        table.Write("label", "AB");
        Assert.Equal(new ushort[] { 0x4142, 0x0000, 0x0000 }, Words(0, 3));
        Assert.Equal("AB", table.Read<string>("label"));

        table.Write("label", "ABC");
        Assert.Equal(new ushort[] { 0x4142, 0x4300, 0x0000 }, Words(0, 3));
        Assert.Equal("ABC", table.Read<string>("label"));

        table.Write("label", "ABCDE"); // exactly length bytes: no terminator needed
        Assert.Equal(new ushort[] { 0x4142, 0x4344, 0x4500 }, Words(0, 3));
        Assert.Equal("ABCDE", table.Read<string>("label"));

        table.Write("label", "");
        Assert.Equal(new ushort[] { 0, 0, 0 }, Words(0, 3));
        Assert.Equal("", table.Read<string>("label"));
    }

    [Fact]
    public void String_Utf8_CountsBytes()
    {
        var table = Table(Str("label", 0, 5));
        table.Write("label", "日"); // 3 bytes E6 97 A5
        Assert.Equal(new ushort[] { 0xE697, 0xA500, 0x0000 }, Words(0, 3));
        Assert.Equal("日", table.Read<string>("label"));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Write("label", "日本")); // 6 bytes > 5
    }

    [Fact]
    public void String_TooLong_Throws_DeviceUntouched()
    {
        var table = Table(Str("label", 0, 5));
        table.Write("label", "AB");
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Write("label", "ABCDEF"));
        Assert.Equal("AB", table.Read<string>("label"));
    }

    [Fact]
    public void String_ReadStopsAtNul_InvalidUtf8_Replacement()
    {
        var table = Table(Str("label", 0, 5));
        _device.Write(TableKind.Words, 0, new ushort[] { 0x41FF, 0x0042, 0x4343 });
        Assert.Equal("A�", table.Read<string>("label"));
    }

    [Fact]
    public void String_HostTypeIsString()
    {
        var table = Table(Str("label", 0, 5));
        Assert.Throws<ArgumentException>(() => table.Read<int>("label"));
        Assert.Throws<ArgumentException>(() => table.Write("label", 1));
    }

    [Fact]
    public void String_Array_ElementsAreSpaced()
    {
        var table = Table(Str("names", 0, 3, count: 2)); // 2 words per element
        table.WriteAll("names", new[] { "ab", "cde" });
        Assert.Equal(new ushort[] { 0x6162, 0x0000, 0x6364, 0x6500 }, Words(0, 4));
        Assert.Equal("cde", table.Read<string>("names", 1));
        Assert.Equal(new[] { "ab", "cde" }, table.ReadAll<string>("names"));
    }

    // --- time types ----------------------------------------------------------------

    [Fact]
    public void Time_IsInt32Ms_BigEndian()
    {
        var table = Table(new Variable("timer", TableKind.Words, 0, VariableType.Time));
        table.Write("timer", -1500);
        Assert.Equal(new ushort[] { 0xFFFF, 0xFA24 }, Words(0, 2));
        Assert.Equal(-1500, table.Read<int>("timer"));
    }

    [Fact]
    public void Tod_IsUInt32Ms()
    {
        var table = Table(new Variable("tod", TableKind.Words, 0, VariableType.Tod));
        table.Write("tod", 86399999u);
        Assert.Equal(new ushort[] { 0x0526, 0x5BFF }, Words(0, 2));
        Assert.Equal(86399999u, table.Read<uint>("tod"));
    }

    [Fact]
    public void Date_And_Dt_AreUInt64Seconds()
    {
        var table = Table(
            new Variable("day", TableKind.Words, 0, VariableType.Date),
            new Variable("stamp", TableKind.Words, 4, VariableType.Dt));
        table.Write("day", 1_793_491_200ul); // 2026-11-01T00:00:00Z
        table.Write("stamp", 1_793_525_400ul); // 2026-11-01T09:30:00Z
        Assert.Equal(new ushort[] { 0, 0, 0x6AE6, 0x8100 }, Words(0, 4));
        Assert.Equal(1_793_491_200ul, table.Read<ulong>("day"));
        Assert.Equal(1_793_525_400ul, table.Read<ulong>("stamp"));
    }

    [Fact]
    public void TimeTypes_HostTypes()
    {
        var table = Table(
            new Variable("timer", TableKind.Words, 0, VariableType.Time),
            new Variable("stamp", TableKind.Words, 4, VariableType.Dt));
        Assert.Throws<ArgumentException>(() => table.Read<uint>("timer"));
        Assert.Throws<ArgumentException>(() => table.Read<long>("stamp"));
    }

    // --- change log formatting -----------------------------------------------------

    private static (VariableTable Table, CapturingProvider Log) Logged(Device device, params Variable[] vars)
    {
        var provider = new CapturingProvider();
        return (new VariableTable(device, vars, provider.CreateLogger("test")), provider);
    }

    [Fact]
    public void ChangeLog_String_Quoted_Escaped()
    {
        var (table, log) = Logged(_device, Str("label", 0, 8));
        table.Write("label", "a\"b\\c");
        Assert.Contains(log.Entries, e => e.EventId == 1101 && e.Message == "plc-a label=\"a\\\"b\\\\c\"");
    }

    [Fact]
    public void ChangeLog_Time_Milliseconds_Dt_Iso8601()
    {
        var (table, log) = Logged(_device,
            new Variable("timer", TableKind.Words, 0, VariableType.Time),
            new Variable("stamp", TableKind.Words, 4, VariableType.Dt),
            new Variable("day", TableKind.Words, 8, VariableType.Date));
        table.Write("timer", 1500);
        table.Write("stamp", 1_793_525_400ul);
        table.Write("day", 1_793_491_200ul);
        Assert.Contains(log.Entries, e => e.Message == "plc-a timer=1500");
        Assert.Contains(log.Entries, e => e.Message == "plc-a stamp=2026-11-01T09:30:00Z");
        Assert.Contains(log.Entries, e => e.Message == "plc-a day=2026-11-01T00:00:00Z");
    }
}
