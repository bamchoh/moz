using Moz.Devices;

namespace Moz.Modbus;

/// <summary>Result of handling one request PDU.</summary>
/// <param name="Length">Response length in bytes written to the caller's buffer.</param>
/// <param name="ExceptionCode">0 for a normal response; otherwise the Modbus exception code.</param>
public readonly record struct PduOutcome(int Length, byte ExceptionCode);

/// <summary>
/// The pure Modbus PDU codec: decodes a request PDU (function code + data,
/// NO MBAP header, NO unit id, NO CRC), applies it to the device, and encodes
/// the response PDU — including exception responses. No I/O, no logging;
/// transports log. This is the largest unit-test surface of moz.
///
/// Byte order: registers are big-endian. Word/byte swap options are
/// deliberately absent (docs/spec-modbus.md).
/// </summary>
public static class ModbusPdu
{
    private const byte ExceptionBit = 0x80;

    /// <summary>
    /// Handles one request PDU. The response is written into
    /// <paramref name="resp"/> (caller-owned; at least 260 bytes). Throws
    /// only on programming bugs (bad span sizes); every protocol-level
    /// problem becomes an exception response.
    /// </summary>
    public static PduOutcome Handle(ReadOnlySpan<byte> pdu, IDeviceAccess device, Span<byte> resp)
    {
        if (pdu.Length == 0)
        {
            return Exception(resp, (byte)FunctionCode.ReadCoils, ExceptionCode.IllegalDataValue);
        }
        return (FunctionCode)pdu[0] switch
        {
            FunctionCode.ReadCoils => ReadBits(pdu, device, resp, TableKind.Coil),
            FunctionCode.ReadDiscreteInputs => ReadBits(pdu, device, resp, TableKind.DiscreteInput),
            FunctionCode.ReadHoldingRegisters => ReadRegs(pdu, device, resp, TableKind.HoldingRegister),
            FunctionCode.ReadInputRegisters => ReadRegs(pdu, device, resp, TableKind.InputRegister),
            FunctionCode.WriteSingleCoil => WriteSingleCoil(pdu, device, resp),
            FunctionCode.WriteSingleRegister => WriteSingleRegister(pdu, device, resp),
            FunctionCode.WriteMultipleCoils => WriteMultipleBits(pdu, device, resp),
            FunctionCode.WriteMultipleRegisters => WriteMultipleRegs(pdu, device, resp),
            _ => Exception(resp, pdu[0], ExceptionCode.IllegalFunction),
        };
    }

    // --- reads (FC01/02 bits, FC03/04 words) -----------------------------

    private static PduOutcome ReadBits(ReadOnlySpan<byte> pdu, IDeviceAccess device, Span<byte> resp, TableKind table)
    {
        // request: fc, addr(2), qty(2) => 7 bytes
        if (pdu.Length != 5)
        {
            return BadValue(resp, pdu[0]);
        }
        ushort addr = BigEndianUInt16(pdu[1..]);
        ushort qty = BigEndianUInt16(pdu[3..]);

        if (qty < 1 || qty > ModbusLimits.ReadBitsMax)
        {
            return BadValue(resp, pdu[0]);
        }
        if ((uint)addr + qty > (uint)device.Length(table))
        {
            return BadAddress(resp, pdu[0]);
        }

        Span<bool> bits = stackalloc bool[ModbusLimits.ReadBitsMax];
        device.Read(table, addr, bits[..qty]);

        int byteCount = (qty + 7) / 8;
        resp[0] = pdu[0];
        resp[1] = (byte)byteCount;
        for (int i = 0; i < qty; i++)
        {
            if (bits[i])
            {
                resp[2 + i / 8] |= (byte)(1 << (i % 8));
            }
        }
        return new PduOutcome(2 + byteCount, 0);
    }

    private static PduOutcome ReadRegs(ReadOnlySpan<byte> pdu, IDeviceAccess device, Span<byte> resp, TableKind table)
    {
        // request: fc, addr(2), qty(2) => 7 bytes
        if (pdu.Length != 5)
        {
            return BadValue(resp, pdu[0]);
        }
        ushort addr = BigEndianUInt16(pdu[1..]);
        ushort qty = BigEndianUInt16(pdu[3..]);

        if (qty < 1 || qty > ModbusLimits.ReadRegsMax)
        {
            return BadValue(resp, pdu[0]);
        }
        if ((uint)addr + qty > (uint)device.Length(table))
        {
            return BadAddress(resp, pdu[0]);
        }

        Span<ushort> words = stackalloc ushort[ModbusLimits.ReadRegsMax];
        device.Read(table, addr, words[..qty]);

        int byteCount = qty * 2;
        resp[0] = pdu[0];
        resp[1] = (byte)byteCount;
        for (int i = 0; i < qty; i++)
        {
            PutUInt16BigEndian(resp[(2 + i * 2)..], words[i]);
        }
        return new PduOutcome(2 + byteCount, 0);
    }

    // --- writes (FC05/06 single, FC15/16 multiple) -----------------------

    private static PduOutcome WriteSingleCoil(ReadOnlySpan<byte> pdu, IDeviceAccess device, Span<byte> resp)
    {
        // request: fc, addr(2), value(2) => 7 bytes
        if (pdu.Length != 5)
        {
            return BadValue(resp, pdu[0]);
        }
        ushort addr = BigEndianUInt16(pdu[1..]);
        ushort value = BigEndianUInt16(pdu[3..]);

        if (value is not (0x0000 or 0xFF00))
        {
            return BadValue(resp, pdu[0]);
        }
        if ((uint)addr >= (uint)device.Length(TableKind.Coil))
        {
            return BadAddress(resp, pdu[0]);
        }

        device.WriteBit(TableKind.Coil, addr, value == 0xFF00);
        pdu.CopyTo(resp[..5]); // FC05 echoes the request
        return new PduOutcome(5, 0);
    }

    private static PduOutcome WriteSingleRegister(ReadOnlySpan<byte> pdu, IDeviceAccess device, Span<byte> resp)
    {
        // request: fc, addr(2), value(2) => 7 bytes
        if (pdu.Length != 5)
        {
            return BadValue(resp, pdu[0]);
        }
        ushort addr = BigEndianUInt16(pdu[1..]);
        ushort value = BigEndianUInt16(pdu[3..]);

        if ((uint)addr >= (uint)device.Length(TableKind.HoldingRegister))
        {
            return BadAddress(resp, pdu[0]);
        }

        Span<ushort> one = [value];
        device.Write(TableKind.HoldingRegister, addr, one);
        pdu.CopyTo(resp[..5]); // FC06 echoes the request
        return new PduOutcome(5, 0);
    }

    private static PduOutcome WriteMultipleBits(ReadOnlySpan<byte> pdu, IDeviceAccess device, Span<byte> resp)
    {
        // request: fc, addr(2), qty(2), byteCount(1), data(byteCount) => 9+byteCount
        if (pdu.Length < 7)
        {
            return BadValue(resp, pdu[0]);
        }
        ushort addr = BigEndianUInt16(pdu[1..]);
        ushort qty = BigEndianUInt16(pdu[3..]);
        byte byteCount = pdu[5];

        int expectedBytes = (qty + 7) / 8;
        if (qty < 1 || qty > ModbusLimits.WriteBitsMax || byteCount != expectedBytes || pdu.Length != 6 + byteCount)
        {
            return BadValue(resp, pdu[0]);
        }
        if ((uint)addr + qty > (uint)device.Length(TableKind.Coil))
        {
            return BadAddress(resp, pdu[0]);
        }

        Span<bool> bits = stackalloc bool[qty];
        for (int i = 0; i < qty; i++)
        {
            bits[i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
        }
        device.Write(TableKind.Coil, addr, bits);

        // response echoes fc, addr, qty (no byteCount, no data)
        resp[0] = pdu[0];
        BigEndian(resp[1..], addr);
        BigEndian(resp[3..], qty);
        return new PduOutcome(5, 0);
    }

    private static PduOutcome WriteMultipleRegs(ReadOnlySpan<byte> pdu, IDeviceAccess device, Span<byte> resp)
    {
        // request: fc, addr(2), qty(2), byteCount(1), data(byteCount=2*qty) => 9+2*qty
        if (pdu.Length < 7)
        {
            return BadValue(resp, pdu[0]);
        }
        ushort addr = BigEndianUInt16(pdu[1..]);
        ushort qty = BigEndianUInt16(pdu[3..]);
        byte byteCount = pdu[5];

        if (qty < 1 || qty > ModbusLimits.WriteRegsMax || byteCount != qty * 2 || pdu.Length != 6 + byteCount)
        {
            return BadValue(resp, pdu[0]);
        }
        if ((uint)addr + qty > (uint)device.Length(TableKind.HoldingRegister))
        {
            return BadAddress(resp, pdu[0]);
        }

        Span<ushort> words = stackalloc ushort[qty];
        for (int i = 0; i < qty; i++)
        {
            words[i] = BigEndianUInt16(pdu[(6 + i * 2)..]);
        }
        device.Write(TableKind.HoldingRegister, addr, words);

        resp[0] = pdu[0];
        BigEndian(resp[1..], addr);
        BigEndian(resp[3..], qty);
        return new PduOutcome(5, 0);
    }

    // --- helpers ----------------------------------------------------------

    private static PduOutcome BadAddress(Span<byte> resp, byte fc) =>
        Exception(resp, fc, ExceptionCode.IllegalDataAddress);

    private static PduOutcome BadValue(Span<byte> resp, byte fc) =>
        Exception(resp, fc, ExceptionCode.IllegalDataValue);

    /// <summary>
    /// Encode an exception response: fc|0x80, exception code. Public because
    /// the transport's "unexpected failure" chute (exception 04) also uses it.
    /// </summary>
    public static PduOutcome EncodeException(Span<byte> resp, FunctionCode fc, ExceptionCode code) =>
        Exception(resp, (byte)fc, code);

    /// <summary>Encode an exception response: fc|0x80, exception code.</summary>
    private static PduOutcome Exception(Span<byte> resp, byte fc, ExceptionCode code)
    {
        resp[0] = (byte)(fc | ExceptionBit);
        resp[1] = (byte)code;
        return new PduOutcome(2, (byte)code);
    }

    private static ushort BigEndianUInt16(ReadOnlySpan<byte> src) => (ushort)((src[0] << 8) | src[1]);

    private static void BigEndian(Span<byte> dest, ushort value)
    {
        dest[0] = (byte)(value >> 8);
        dest[1] = (byte)value;
    }

    private static void PutUInt16BigEndian(Span<byte> dest, ushort value) => BigEndian(dest, value);
}