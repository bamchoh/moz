using System.Text;

namespace Moz.Modbus.Rtu;

/// <summary>
/// Pure RTU ADU framing: [slave address(1)] [PDU] [CRC16 LSB first].
/// No streams — the server owns the byte loop; this type owns the math.
/// </summary>
public static class Rtu
{
    /// <summary>Maximum ADU size: address + PDU + CRC.</summary>
    public const int AduMaxSize = 1 + ModbusLimits.PduMaxLength + 2;

    /// <summary>Encode an ADU (address + PDU, CRC appended) into a fresh buffer.</summary>
    public static byte[] Frame(byte address, ReadOnlySpan<byte> pdu)
    {
        var adu = new byte[1 + pdu.Length + 2];
        adu[0] = address;
        pdu.CopyTo(adu.AsSpan(1));
        var crc = Crc16.Compute(adu.AsSpan(0, 1 + pdu.Length));
        adu[^2] = (byte)(crc & 0xFF); // LSB first on the wire
        adu[^1] = (byte)(crc >> 8);
        return adu;
    }

    /// <summary>
    /// Parse a complete request ADU. A request's total length is derivable
    /// from the function code, so exact-length frames parse and everything
    /// else (partial / overlong / bad CRC / unknown FC) reports false — the
    /// byte loop keeps accumulating until the idle gap discards it.
    /// </summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> frame, out byte address, out ReadOnlySpan<byte> pdu)
    {
        address = 0;
        pdu = default;
        if (frame.Length < 8)
        {
            return false; // even the shortest request is addr+fc+4+crc
        }
        var fc = frame[1];
        int? length = fc switch
        {
            0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 => 8,
            0x0F or 0x10 => frame.Length < 7 ? null : FrameLengthForMultipleWrite(frame),
            _ => null, // unknown FC: let the idle gap end it
        };
        if (length is null || frame.Length != length.Value)
        {
            return false;
        }
        var crc = Crc16.Compute(frame[..^2]);
        if (frame[^2] != (byte)(crc & 0xFF) || frame[^1] != (byte)(crc >> 8))
        {
            return false;
        }
        address = frame[0];
        pdu = frame[1..^2];
        return true;
    }

    /// <summary>FC15/FC16: total = 7 (addr, fc, addr2, qty2, byteCount) + byteCount + 2 (CRC).</summary>
    private static int? FrameLengthForMultipleWrite(ReadOnlySpan<byte> frame)
    {
        var byteCount = frame[6];
        if (byteCount > ModbusLimits.PduMaxLength - 6)
        {
            return null; // absurd byte count: never parseable
        }
        return 9 + byteCount;
    }

    /// <summary><see cref="ResponseLength"/>: more bytes are needed before the length is known.</summary>
    public const int NeedMore = 0;

    /// <summary><see cref="ResponseLength"/>: the FC can never frame (the master never sent it).</summary>
    public const int Invalid = -1;

    /// <summary>
    /// Master side: the total ADU length of a response, derived from the
    /// function code (and, for reads, the byte count) as the bytes arrive.
    /// Exception responses are 5 bytes, write echoes 8, reads 5 + byteCount.
    /// </summary>
    public static int ResponseLength(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return NeedMore;
        }
        var fc = frame[1];
        if ((fc & 0x80) != 0)
        {
            return (fc & 0x7F) is 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x0F or 0x10 ? 5 : Invalid;
        }
        return fc switch
        {
            0x01 or 0x02 or 0x03 or 0x04 => frame.Length < 3 ? NeedMore : 5 + frame[2],
            0x05 or 0x06 or 0x0F or 0x10 => 8,
            _ => Invalid,
        };
    }

    /// <summary>Check a response's trailing CRC.</summary>
    public static bool CrcOk(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4)
        {
            return false;
        }
        var crc = Crc16.Compute(frame[..^2]);
        return frame[^2] == (byte)(crc & 0xFF) && frame[^1] == (byte)(crc >> 8);
    }

    /// <summary>Debug hex dump (test/debug convenience).</summary>
    public static string HexDump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}