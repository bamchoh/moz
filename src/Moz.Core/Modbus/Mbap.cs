namespace Moz.Modbus;

/// <summary>Parsed MBAP header. MBAP: transaction id(2), protocol id(2), length(2), unit id(1).</summary>
/// <param name="TransactionId">Echoed by the response.</param>
/// <param name="Length">Bytes following the header (unit id + PDU); 0 means the raw field was 0.</param>
/// <param name="UnitId">Slave address seen by the master.</param>
public readonly record struct MbapHeader(ushort TransactionId, ushort Length, byte UnitId);

/// <summary>
/// Pure MBAP framing helpers — no sockets, so header math is unit-testable.
/// The header is parsed alone (7 bytes); the PDU body is read after it.
/// </summary>
public static class Mbap
{
    public const int HeaderSize = 7;

    /// <summary>
    /// Parse the 7-byte header and validate the protocol id and the length
    /// field range (length counts unit id + PDU, so 2..254). Returns false
    /// when malformed — callers log and close (docs/spec-modbus.md policy).
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> header, out MbapHeader parsed)
    {
        parsed = default;
        if (header.Length < HeaderSize)
        {
            return false;
        }
        if (header[2] != 0 || header[3] != 0)
        {
            return false; // protocol id must be 0
        }
        int length = (header[4] << 8) | header[5];
        if (length < 2 || length > ModbusLimits.PduMaxLength + 1)
        {
            return false; // lying length field
        }
        parsed = new MbapHeader(
            (ushort)((header[0] << 8) | header[1]),
            (ushort)length,
            header[6]);
        return true;
    }

    /// <summary>Write a response header into the first 7 bytes of <paramref name="dest"/>.</summary>
    public static void WriteHeader(Span<byte> dest, ushort transactionId, byte unitId, int pduLength)
    {
        dest[0] = (byte)(transactionId >> 8);
        dest[1] = (byte)transactionId;
        dest[2] = 0; // protocol id high
        dest[3] = 0; // protocol id low
        dest[4] = (byte)((pduLength + 1) >> 8);
        dest[5] = (byte)(pduLength + 1);
        dest[6] = unitId;
    }
}