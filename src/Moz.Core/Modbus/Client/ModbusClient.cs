namespace Moz.Modbus.Client;

/// <summary>
/// The request-BUILDING half of the Modbus codec (the server half lives in
/// <see cref="ModbusPdu"/>). Pure functions: build FC01-04 read requests and
/// parse their responses; build FC05/06/15/16 write requests and check their
/// echoes. No sockets, no logging.
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

    /// <summary>
    /// Build a bit write request: FC05 [fc] [addr] [FF00|0000] (exactly one bit)
    /// or FC15 [fc] [addr] [qty] [byteCount] [bits LSB-first].
    /// </summary>
    public static byte[] BuildWriteRequest(int fc, int remoteOffset, ReadOnlySpan<bool> bits)
    {
        if (fc is not (5 or 15))
        {
            throw new ArgumentException($"bit write is FC05/FC15, got {fc}", nameof(fc));
        }
        CheckWriteRange(remoteOffset, bits.Length, fc == 5 ? 1 : ModbusLimits.WriteBitsMax);
        if (fc == 5)
        {
            return [(byte)fc, (byte)(remoteOffset >> 8), (byte)remoteOffset, bits[0] ? (byte)0xFF : (byte)0x00, 0x00];
        }
        int byteCount = (bits.Length + 7) / 8;
        var pdu = new byte[6 + byteCount];
        pdu[0] = (byte)fc;
        pdu[1] = (byte)(remoteOffset >> 8);
        pdu[2] = (byte)remoteOffset;
        pdu[3] = (byte)(bits.Length >> 8);
        pdu[4] = (byte)bits.Length;
        pdu[5] = (byte)byteCount;
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                pdu[6 + i / 8] |= (byte)(1 << (i % 8));
            }
        }
        return pdu;
    }

    /// <summary>
    /// Build a word write request: FC06 [fc] [addr] [value] (exactly one word)
    /// or FC16 [fc] [addr] [qty] [byteCount] [words big-endian].
    /// </summary>
    public static byte[] BuildWriteRequest(int fc, int remoteOffset, ReadOnlySpan<ushort> words)
    {
        if (fc is not (6 or 16))
        {
            throw new ArgumentException($"word write is FC06/FC16, got {fc}", nameof(fc));
        }
        CheckWriteRange(remoteOffset, words.Length, fc == 6 ? 1 : ModbusLimits.WriteRegsMax);
        if (fc == 6)
        {
            return [(byte)fc, (byte)(remoteOffset >> 8), (byte)remoteOffset, (byte)(words[0] >> 8), (byte)words[0]];
        }
        var pdu = new byte[6 + words.Length * 2];
        pdu[0] = (byte)fc;
        pdu[1] = (byte)(remoteOffset >> 8);
        pdu[2] = (byte)remoteOffset;
        pdu[3] = (byte)(words.Length >> 8);
        pdu[4] = (byte)words.Length;
        pdu[5] = (byte)(words.Length * 2);
        for (int i = 0; i < words.Length; i++)
        {
            pdu[6 + i * 2] = (byte)(words[i] >> 8);
            pdu[7 + i * 2] = (byte)words[i];
        }
        return pdu;
    }

    /// <summary>
    /// Check a write response against its request. FC05/06 echo the request
    /// ([fc] [addr] [value]); FC15/16 answer [fc] [addr] [qty]. Throws on an
    /// exception response or any mismatch.
    /// </summary>
    public static void CheckWriteResponse(int fc, int remoteOffset, int count, ReadOnlySpan<byte> pdu)
    {
        if (fc is not (5 or 6 or 15 or 16))
        {
            throw new ArgumentException($"write check is for FC05/06/15/16, got {fc}", nameof(fc));
        }
        if (pdu.Length == 0 || (pdu[0] & 0x7F) != fc)
        {
            throw new ArgumentException($"response fc mismatch: expected {fc:X2}, got {AsHex(pdu)}");
        }
        if ((pdu[0] & 0x80) != 0)
        {
            throw new ArgumentException($"remote returned exception {ExceptionText(pdu)}");
        }
        if (pdu.Length != 5)
        {
            throw new ArgumentException($"response length mismatch: got {AsHex(pdu)} for fc={fc}");
        }
        int addr = (pdu[1] << 8) | pdu[2];
        if (addr != remoteOffset)
        {
            throw new ArgumentException($"response address {addr} ≠ request {remoteOffset} (fc={fc})");
        }
        if (fc is 15 or 16)
        {
            int qty = (pdu[3] << 8) | pdu[4];
            if (qty != count)
            {
                throw new ArgumentException($"response quantity {qty} ≠ request {count} (fc={fc})");
            }
        }
    }

    private static void CheckWriteRange(int remoteOffset, int count, int max)
    {
        if (remoteOffset < 0 || remoteOffset > ushort.MaxValue || count < 1 || count > max)
        {
            throw new ArgumentException(
                $"offset/count out of range: offset={remoteOffset} count={count} (limit {max})", nameof(count));
        }
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