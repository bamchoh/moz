using System.Text;
using Moz.Devices;
using Moz.Modbus;

namespace Moz.Tests;

/// <summary>
/// Table-driven hex vectors for the PDU codec. These tests ARE the exception
/// matrix of docs/spec-modbus.md — keep them in sync with the doc.
/// </summary>
public class ModbusPduTests
{
    private static byte[] Hex(string s)
    {
        s = s.Replace(" ", "");
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private static string ToHex(ReadOnlySpan<byte> data, int length)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static ushort[] Words(params ushort[] values) => values;

    /// <summary>Run one request against a fresh device; returns the response as hex.</summary>
    private static (string Response, byte Exc) Run(string requestHex, Device? device = null)
    {
        device ??= new Device("t");
        var req = Hex(requestHex);
        Span<byte> resp = stackalloc byte[260];
        var outcome = ModbusPdu.Handle(req, device, resp);
        return (ToHex(resp, outcome.Length), outcome.ExceptionCode);
    }

    // --- FC03 Read Holding Registers --------------------------------------

    [Fact]
    public void FC03_ReadHolding_Seeded()
    {
        var device = new Device("t");
        device.Seed(TableKind.Words, 0, Words(0x1234, 0xABCD));
        var (resp, exc) = Run("0300000002", device);
        Assert.Equal("03041234ABCD", resp);
        Assert.Equal(0, exc);
    }

    [Fact]
    public void FC03_AtOffset()
    {
        var device = new Device("t");
        device.Seed(TableKind.Words, 0, Words(9, 8, 7));
        var (resp, _) = Run("0300010002", device); // addr=1 qty=2
        Assert.Equal("030400080007", resp);
    }

    [Theory]
    [InlineData("0300000000", "8303")]  // qty 0
    [InlineData("030000007E", "8303")]  // qty 126 > 125
    [InlineData("03FF000001", "8302")]  // addr 0xFF00+1 beyond
    [InlineData("03", "8303")]          // truncated
    [InlineData("9900", "9901")]        // unknown FC 0x99
    [InlineData("0900000001", "8901")]  // unknown FC 0x09
    public void Exceptions_Matrix(string req, string expected)
    {
        var (resp, exc) = Run(req, new Device("t", words: 10));
        Assert.Equal(expected, resp);
        byte expectedExc = expected[3] == '2' ? (byte)2 : expected[3] == '3' ? (byte)3 : (byte)1;
        Assert.Equal(expectedExc, exc);
    }

    [Theory]
    [InlineData("0400000001", "04020000")]   // input registers start at 0
    public void FC04_ReadInputRegisters(string req, string expected)
    {
        Assert.Equal(expected, Run(req, new Device("t")).Response);
    }

    [Fact]
    public void FC04_Seeded()
    {
        var device = new Device("t");
        device.Seed(TableKind.ReadOnlyWords, 0, Words(0xBEEF));
        Assert.Equal("0402BEEF", Run("0400000001", device).Response);
    }

    [Fact]
    public void FC03_Boundary_125OK_126Fails()
    {
        var (resp, _) = Run("030000007D", new Device("t", words: 125));
        Assert.Equal("03FA", resp[..4]);
        var (bad, exc) = Run("030000007E", new Device("t", words: 125));
        Assert.Equal("8303", bad);
        Assert.Equal((byte)3, exc);
    }

    // --- FC01/FC02 bit reads ------------------------------------------------

    [Fact]
    public void FC01_ReadCoils_Packed()
    {
        var device = new Device("t");
        device.WriteBit(TableKind.Bits, 0, true);
        device.WriteBit(TableKind.Bits, 9, true); // second byte, bit 1
        // bits: bit0=1 → byte0 %00000001 = 0x01; bit9=1 → byte1 = 0x02
        Assert.Equal("01020102", Run("010000000A", device).Response);
    }

    [Fact]
    public void FC01_Boundary_2000OK_2001Fails()
    {
        var device = new Device("t", bits: 2000);
        var (resp, _) = Run("01000007D0", device);
        Assert.Equal("01FA", resp[..4]);
        var (bad, _) = Run("01000007D1", device);
        Assert.Equal("8103", bad);
    }

    [Fact]
    public void FC02_SeededDiscrete()
    {
        var device = new Device("t", readOnlyBits: 10);
        device.Seed(TableKind.ReadOnlyBits, 0, (bool[])[true, false, true]);
        Assert.Equal("020105", Run("0200000003", device).Response);
    }

    [Fact]
    public void FC02_BeyondRange()
    {
        var device = new Device("t", readOnlyBits: 10);
        Assert.Equal("8202", Run("02000A0001", device).Response);
    }

    // --- FC05/FC06 single writes ----------------------------------------------

    [Fact]
    public void FC05_WriteSingleCoil_EchoAndApply()
    {
        var device = new Device("t");
        var (resp, exc) = Run("050005FF00", device);
        Assert.Equal("050005FF00", resp);
        Assert.Equal(0, exc);
        Assert.True(device.ReadBit(TableKind.Bits, 5));
        Assert.False(device.ReadBit(TableKind.Bits, 6));
    }

    [Theory]
    [InlineData("0500050002", "8503")]  // value not 0x0000/0xFF00
    [InlineData("05FFFF0000", "8502")]  // addr beyond
    [InlineData("05", "8503")]          // truncated
    public void FC05_Exceptions(string req, string expected)
    {
        Assert.Equal(expected, Run(req, new Device("t", bits: 32)).Response);
    }

    [Fact]
    public void FC06_WriteSingleRegister_EchoAndApply()
    {
        var device = new Device("t");
        var (resp, _) = Run("06000ACAFE", device); // addr=0x000A value=0xCAFE
        Assert.Equal("06000ACAFE", resp);
        Assert.Equal((ushort)0xCAFE, ReadWord(device, 10));
    }

    private static ushort ReadWord(Device device, int index)
    {
        Span<ushort> w = stackalloc ushort[1];
        device.Read(TableKind.Words, index, w);
        return w[0];
    }

    [Theory]
    [InlineData("06FFFFFFFF", "8602")]
    [InlineData("06FFFF", "8603")]
    public void FC06_Exceptions(string req, string expected)
    {
        Assert.Equal(expected, Run(req, new Device("t", words: 1)).Response);
    }

    // --- FC15/FC16 multiple writes ----------------------------------------------

    [Fact]
    public void FC15_WriteMultipleCoils_LSBFirst()
    {
        var device = new Device("t", bits: 32);
        var (resp, _) = Run("0F0000000A02FF03", device); // addr0 qty10, bytes FF 03
        Assert.Equal("0F0000000A", resp);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(device.ReadBit(TableKind.Bits, i));
        }
        Assert.False(device.ReadBit(TableKind.Bits, 10));
    }

    [Fact]
    public void FC15_UnwrittenCoilsOutsideQtyKeepValue()
    {
        var device = new Device("t", bits: 32);
        device.WriteBit(TableKind.Bits, 5, true);
        var (r1, _) = Run("0F0000000801FF", device); // write coils 0..7 = true (qty=8)
        Assert.Equal("0F00000008", r1);
        Assert.True(device.ReadBit(TableKind.Bits, 5));       // within qty: set
        var (r2, _) = Run("0F000000010100", device);          // write coil 0..1 = false
        Assert.Equal("0F00000001", r2);
        Assert.True(device.ReadBit(TableKind.Bits, 5));       // outside qty: kept
        Assert.False(device.ReadBit(TableKind.Bits, 0));
    }

    [Theory]
    [InlineData("0F0000000000", "8F03")]     // qty 0
    [InlineData("0F0000001001FF", "8F03")]   // qty 16 but byteCount 1
    [InlineData("0F0000001402FFFF", "8F03")] // byteCount 2 but ceil(20/8)=3
    [InlineData("0F0000000902FF", "8F03")] // data shorter than byteCount
    [InlineData("0FF000000101FF", "8F02")]   // addr 0xF000 qty1 beyond 32
    [InlineData("0F", "8F03")]               // truncated
    public void FC15_Exceptions(string req, string expected)
    {
        Assert.Equal(expected, Run(req, new Device("t", bits: 32)).Response);
    }

    [Fact]
    public void FC15_Boundary_1968OK()
    {
        var req = "0F000007B0F6" + new string('F', 492); // qty=1968 (0x7B0), byteCount=246
        var (resp, _) = Run(req, new Device("t", bits: 2000));
        Assert.Equal("0F000007B0", resp);
    }

    [Fact]
    public void FC15_Boundary_1969Fails()
    {
        var req = "0F000007B1F7" + new string('F', 494); // qty=1969 (0x7B1) > 1968
        Assert.Equal("8F03", Run(req, new Device("t", bits: 2000)).Response);
    }

    [Fact]
    public void FC16_WriteMultipleRegisters_BigEndian()
    {
        var device = new Device("t");
        var (resp, _) = Run("1000000002041234ABCD", device);
        Assert.Equal("1000000002", resp);
        Assert.Equal(0x1234, ReadWord(device, 0));
        Assert.Equal(0xABCD, ReadWord(device, 1));
    }

    [Theory]
    [InlineData("1000000000041234ABCD", "9003")] // qty 0
    [InlineData("1000000001041234ABCD", "9003")] // byteCount != 2*qty
    [InlineData("1000000002041234", "9003")]     // data truncated vs byteCount
    [InlineData("10FFFF0001021234", "9002")]     // beyond length 1
    [InlineData("10", "9003")]                   // truncated
    public void FC16_Exceptions(string req, string expected)
    {
        Assert.Equal(expected, Run(req, new Device("t", words: 1)).Response);
    }

    [Fact]
    public void FC16_Boundary_123OK_124Fails()
    {
        var req = "100000007BF6" + new string('7', 492); // qty=123, byteCount=246
        Assert.Equal("100000007B", Run(req, new Device("t", words: 200)).Response);
        var bad = "100000007C F7" + new string('7', 494);
        Assert.Equal("9003", Run(bad, new Device("t", words: 200)).Response);
    }

    [Fact]
    public void EmptyPdu_IllegalDataValue()
    {
        Assert.Equal("8103", Run("", new Device("t")).Response);
    }

    [Fact]
    public void RangeOverflow_AddrPlusQty_UInt16Wraps()
    {
        // addr = 0xFFF0, qty = 0x0010: naive int addition is safe, but a byte-level
        // overflow check must also reject addr+qty > length (device length small)
        Assert.Equal("8302", Run("03FFF00010", new Device("t", words: 100)).Response);
    }

    /// <summary>
    /// Connections reuse one response buffer: a bit read after a register read
    /// must not OR its bits into the previous response's bytes.
    /// </summary>
    [Fact]
    public void ReadBits_ClearsStaleResponseBytes()
    {
        var device = new Device("t");
        device.Seed(TableKind.ReadOnlyBits, 0, new[] { true, false, true });
        var resp = new byte[300];
        Array.Fill(resp, (byte)0xBE); // leftovers of an earlier FC04 response
        var outcome = ModbusPdu.Handle(Hex("02 0000 0003"), device, resp);
        Assert.Equal(0, outcome.ExceptionCode);
        Assert.Equal("020105", ToHex(resp, outcome.Length));
    }
}
