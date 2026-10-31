namespace Moz.Modbus.Client;

/// <summary>
/// The request-BUILDING half of the Modbus codec (the server half lives in
/// <see cref="ModbusPdu"/>). Pure functions: build FC01-04 read requests and
/// parse their responses. No sockets, no logging.
/// </summary>
public static class ModbusClient
{
    /// <summary>Broadcast/unknown: not used by the poller (see the poller's policy).</summary>
    public const byte ExceptionBit = 0x80;

    /// <summary>Build a read request PDU: [fc] [addr hi] [addr lo] [qty hi] [qty lo].</summary>
    public static byte[] BuildReadRequest(int fc, int remoteOffset, int count)
    {
        bool isWord = fc is 3 or 4;
        int max = isWord ? ModbusLimits.ReadRegsMax : ModbusLimits.ReadBitsMax;
        if (fc is not (1 or 2 or 3 or 4))
        {
            throw new ArgumentException($"poll fc must be 1..4 (reads only), got {fc}", nameof(fc));
        }
        if (remoteOffset < 0 || remoteOffset > ushort.MaxValue || count < 1 || count > max)
        {
            throw new ArgumentException(
                $"offset/count out of range: offset={remoteOffset} count={count} (limit {max})", nameof(count));
        }
        return
        [
            (byte)fc,
            (byte)(remoteOffset >> 8), (byte)remoteOffset,
            (byte)(count >> 8), (byte)count,
        ];
    }

    /// <summary>
    /// Parse a read response for a word FC (03/04): [fc] [byteCount] [qty*2 bytes].
    /// Throws when the response is an exception or inconsistent with the request.
    /// </summary>
    public static ushort[] ParseWordResponse(int fc, int count, ReadOnlySpan<byte> pdu)
    {
        if (fc is not (3 or 4))
        {
            throw new ArgumentException($"word parse is for FC03/FC04, got {fc}", nameof(fc));
        }
        if (pdu.Length == 0 || (pdu[0] & 0x7F) != fc)
        {
            throw new ArgumentException($"response fc mismatch: expected {fc:X2}, got {AsHex(pdu)}");
        }
        if ((pdu[0] & 0x80) != 0)
        {
            throw new ArgumentException($"remote returned exception {ExceptionText(pdu)}");
        }
        int byteCount = pdu.Length > 1 ? pdu[1] : -1;
        if (pdu.Length != 2 + count * 2 || byteCount != count * 2)
        {
            throw new ArgumentException($"response length mismatch: got {AsHex(pdu)} for fc={fc} qty={count}");
        }
        var words = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            words[i] = (ushort)((pdu[2 + i * 2] << 8) | pdu[3 + i * 2]);
        }
        return words;
    }

    /// <summary>
    /// Parse a read response for a bit FC (01/02): [fc] [byteCount] [ceil(count/8) bytes].
    /// Returns count bits, LSB-first within each byte.
    /// </summary>
    public static bool[] ParseBitResponse(int fc, int count, ReadOnlySpan<byte> pdu)
    {
        if (fc is not (1 or 2))
        {
            throw new ArgumentException($"bit parse is for FC01/FC02, got {fc}", nameof(fc));
        }
        if (pdu.Length == 0 || (pdu[0] & 0x7F) != fc)
        {
            throw new ArgumentException($"response fc mismatch: expected {fc:X2}, got {AsHex(pdu)}");
        }
        if ((pdu[0] & 0x80) != 0)
        {
            throw new ArgumentException($"remote returned exception {ExceptionText(pdu)}");
        }
        int expectBytes = (count + 7) / 8;
        int byteCount = pdu.Length > 1 ? pdu[1] : -1;
        if (pdu.Length != 2 + expectBytes || byteCount != expectBytes)
        {
            throw new ArgumentException($"response length mismatch: got {AsHex(pdu)} for fc={fc} qty={count}");
        }
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
        {
            bits[i] = (pdu[2 + i / 8] & (1 << (i % 8))) != 0;
        }
        return bits;
    }

    private static string ExceptionText(ReadOnlySpan<byte> pdu) =>
        $"code {(pdu.Length > 1 ? pdu[1] : 0)} (fc {pdu[0]:X2})";

    private static string AsHex(ReadOnlySpan<byte> pdu)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in pdu)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.Length > 16
            ? sb.ToString(0, 16) + "…"
            : sb.ToString();
    }
}