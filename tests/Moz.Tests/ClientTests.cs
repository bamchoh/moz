using Moz.Modbus;
using Moz.Modbus.Client;

namespace Moz.Tests;

/// <summary>
/// Client (master) role, phase 6: the request-BUILDING half of the codec and
/// the poller that mirrors a remote slave into a local device. Same promise
/// as all phases: moz's TCP/RTU servers + variables keep working unchanged.
/// </summary>
public class ClientTests
{
    // --- request building ------------------------------------------------------

    [Theory]
    [InlineData(3, 107, 3, "03006B0003")]   // FC03 addr=0x006B qty=3
    [InlineData(1, 0, 1, "0100000001")]
    [InlineData(4, 0xFFFF, 1, "04FFFF0001")]
    [InlineData(2, 0x000A, 16, "02000A0010")]
    public void BuildReadRequest_ByteExact(byte fc, int offset, int count, string expectedHex)
    {
        Assert.Equal(expectedHex, Hex.ToHex(ModbusClient.BuildReadRequest(fc, offset, count)));
    }

    [Theory]
    [InlineData(5, 0, 1)]   // write FC as a poll — reads only
    [InlineData(99, 0, 1)]  // unknown FC
    [InlineData(3, 0, 126)] // qty over word limit
    [InlineData(3, -1, 1)]
    [InlineData(1, 0, 2001)]
    [InlineData(3, 0, 0)]
    public void BuildReadRequest_RejectsInvalid(byte fc, int offset, int count)
    {
        Assert.ThrowsAny<Exception>(() => ModbusClient.BuildReadRequest(fc, offset, count));
    }

    // --- response parsing ------------------------------------------------------

    [Fact]
    public void ParseWordResponse_ExtractsBigEndianWords()
    {
        var words = ModbusClient.ParseWordResponse(3, count: 2, Hex.ToBytes("03041234ABCD"));
        Assert.Equal((ushort)0x1234, words[0]);
        Assert.Equal((ushort)0xABCD, words[1]);
    }

    [Fact]
    public void ParseBitResponse_ExtractsLSBFirstBits()
    {
        var bits = ModbusClient.ParseBitResponse(1, count: 3, Hex.ToBytes("010165"));
        // byte 0x65 = 0b01100101 hmm — check: bit0=1,bit1=0,bit2=1? 0x65 = 01100101: b0=1,b1=0,b2=1,b3=0...
        // count=3 → [true,false,true]
        Assert.Equal([true, false, true], bits);
    }

    [Theory]
    [InlineData("830200", 3, 1)]      // exception response: fc|0x80 + code
    [InlineData("03041234", 3, 2)]    // byteCount says 4 but only 2 data bytes → truncated
    [InlineData("02041234ABCD", 3, 2)]// wrong FC echo (0x02 ≠ 0x03)
    [InlineData("03031234ABCD", 3, 2)]// byteCount 3 ≠ 2*qty
    [InlineData("03", 3, 1)]          // too short
    public void ParseWordResponse_RejectsInvalid(string pduHex, byte fc, int count)
    {
        Assert.ThrowsAny<Exception>(() => ModbusClient.ParseWordResponse(fc, count, Hex.ToBytes(pduHex)));
    }

    [Fact]
    public void ParseBitResponse_RejectsBadByteCount()
    {
        // count=3 → bc must be 1; here bc=2
        Assert.ThrowsAny<Exception>(() => ModbusClient.ParseBitResponse(1, 3, Hex.ToBytes("010201")));
    }
}

/// <summary>Hex helpers kept next to the tests that use them.</summary>
internal static class Hex
{
    public static byte[] ToBytes(string s)
    {
        s = s.Replace(" ", "");
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    public static string ToHex(byte[] data)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in data)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}