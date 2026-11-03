using Moz.Mc;

namespace Moz.Tests;

/// <summary>
/// MC protocol, 3E frame, binary code (Q/L-compatible): device notation,
/// request building and response parsing, byte-exact. Vectors follow the
/// MELSEC Communication Protocol reference (network 0, PC 0xFF, module I/O
/// 0x03FF, station 0, monitoring timer 0x0010).
/// </summary>
public class McFrameTests
{
    private static readonly Mc3EOptions Defaults = new();

    // --- device notation --------------------------------------------------------------

    [Theory]
    [InlineData("D100", "D", 0xA8, false, 100)]
    [InlineData("M0", "M", 0x90, true, 0)]
    [InlineData("X1A", "X", 0x9C, true, 0x1A)]     // X/Y/B/W/SB/SW/DX/DY/ZR are hexadecimal
    [InlineData("y1f", "Y", 0x9D, true, 0x1F)]     // case-insensitive
    [InlineData("W100", "W", 0xB4, false, 0x100)]
    [InlineData("SD100", "SD", 0xA9, false, 100)]  // two-letter codes win over S + D...
    [InlineData("S10", "S", 0x98, true, 10)]
    [InlineData("ZR10", "ZR", 0xB0, false, 0x10)]
    [InlineData("TN5", "TN", 0xC2, false, 5)]
    [InlineData("CS3", "CS", 0xC4, true, 3)]
    [InlineData("R16777215", "R", 0xAF, false, 0xFFFFFF)]
    public void Device_Parses(string text, string code, int binary, bool isBit, int address)
    {
        Assert.True(McDevice.TryParse(text, out var device, out var parsed));
        Assert.Equal(code, device.Code);
        Assert.Equal((byte)binary, device.Binary);
        Assert.Equal(isBit, device.IsBit);
        Assert.Equal(address, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("100")]
    [InlineData("Q5")]
    [InlineData("D-1")]
    [InlineData("D1A")]          // D is decimal
    [InlineData("D 1")]
    [InlineData("D16777216")]    // 24-bit head device number
    public void Device_Rejects(string text)
    {
        Assert.False(McDevice.TryParse(text, out _, out _));
    }

    // --- requests ---------------------------------------------------------------------

    [Fact]
    public void ReadRequest_Words()
    {
        // subheader 5000 | net 00 | pc FF | io FF03 | st 00 | len 0C00 | timer 1000 | cmd 0104 | sub 0000 | head 640000 | code A8 | points 0300
        var req = Mc3E.BuildReadRequest(Defaults, Dev("D100", out var a), a, 3);
        Assert.Equal("500000FFFF03000C00100001040000640000A80300", Hex(req));
    }

    [Fact]
    public void ReadRequest_Bits_UsesBitSubcommand()
    {
        var req = Mc3E.BuildReadRequest(Defaults, Dev("M100", out var a), a, 8);
        Assert.Equal("500000FFFF03000C00100001040100640000900800", Hex(req));
    }

    [Fact]
    public void ReadRequest_RoutingAndTimer_FromOptions()
    {
        var options = new Mc3EOptions(Network: 1, Pc: 2, ModuleIo: 0x03D0, ModuleStation: 3, TimerUnits: 40);
        var req = Mc3E.BuildReadRequest(options, Dev("X1A", out var a), a, 16);
        Assert.Equal("50000102D003030C00280001040100" + "1A0000" + "9C" + "1000", Hex(req));
    }

    [Fact]
    public void WriteRequest_Words()
    {
        var req = Mc3E.BuildWriteRequest(Defaults, Dev("D100", out var a), a, new ushort[] { 0x1234, 0x5678 });
        Assert.Equal("500000FFFF03001000100001140000640000A8020034127856", Hex(req));
    }

    [Fact]
    public void WriteRequest_Bits_PacksTwoPointsPerByte_HighNibbleFirst()
    {
        var req = Mc3E.BuildWriteRequest(Defaults, Dev("M100", out var a), a, new[] { true, false, false, true, true });
        Assert.Equal("500000FFFF03000F00100001140100640000900500100110", Hex(req));
    }

    [Theory]
    [InlineData("D0", 0, true)]
    [InlineData("D0", 961, true)]       // > 960 words
    [InlineData("M0", 7169, true)]      // > 7168 bits
    [InlineData("D16777215", 2, true)]  // head + count beyond the 24-bit range
    [InlineData("D0", 960, false)]
    [InlineData("M0", 7168, false)]
    public void Request_CountLimits(string device, int count, bool throws)
    {
        var dev = Dev(device, out var a);
        if (throws)
        {
            Assert.Throws<ArgumentException>(() => Mc3E.BuildReadRequest(Defaults, dev, a, count));
        }
        else
        {
            Assert.NotNull(Mc3E.BuildReadRequest(Defaults, dev, a, count));
        }
    }

    // --- responses --------------------------------------------------------------------

    [Fact]
    public void ResponseHeader_Parses_Length()
    {
        Assert.True(Mc3E.TryParseResponseHeader(Hex("D00000FFFF03000800"), out var length));
        Assert.Equal(8, length);
        Assert.False(Mc3E.TryParseResponseHeader(Hex("500000FFFF03000800"), out _)); // request subheader
        Assert.False(Mc3E.TryParseResponseHeader(Hex("D00000FFFF03000000"), out _)); // no room for the end code
        Assert.False(Mc3E.TryParseResponseHeader(Hex("D00000FFFF0300"), out _));     // short
    }

    [Fact]
    public void ResponseBody_Words_AndEndCode()
    {
        var body = Hex("000034120200FFFF");
        Assert.Equal(0, Mc3E.EndCode(body));
        Assert.Equal([0x1234, 0x0002, 0xFFFF], Mc3E.ParseWords(body, 3));
    }

    [Fact]
    public void ResponseBody_Bits()
    {
        var body = Hex("000010011010");
        Assert.Equal([true, false, false, true, true, false, true, false], Mc3E.ParseBits(body, 8));
        Assert.Equal([true, false, false, true, true], Mc3E.ParseBits(Hex("0000100110"), 5)); // odd count: low nibble of the last byte ignored
    }

    [Fact]
    public void ResponseBody_ErrorEndCode()
    {
        // end code C059 + error information (net, pc, io, st, cmd, sub)
        var body = Hex("59C000FFFF030001040000");
        Assert.Equal(0xC059, Mc3E.EndCode(body));
    }

    [Fact]
    public void ResponseBody_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() => Mc3E.ParseWords(Hex("00003412"), 2));
        Assert.Throws<ArgumentException>(() => Mc3E.ParseBits(Hex("000010"), 3));
        Assert.Throws<ArgumentException>(() => Mc3E.EndCode(Hex("00")));
    }

    // --- helpers ----------------------------------------------------------------------

    private static McDevice Dev(string text, out int address)
    {
        Assert.True(McDevice.TryParse(text, out var device, out address));
        return device;
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(data);
}
