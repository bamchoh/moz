namespace Moz.Modbus.Rtu;

/// <summary>
/// Modbus RTU CRC16: polynomial 0xA001 (reflected 0x8005), init 0xFFFF,
/// bytes consumed LSB-first, LSB of the result travels first on the wire.
/// Vectors: docs/spec-modbus.md + tests (840A, 7687, B880).
/// </summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        return crc;
    }
}