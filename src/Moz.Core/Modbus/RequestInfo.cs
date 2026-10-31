namespace Moz.Modbus;

/// <summary>Logging-only view of a request PDU (shared by TCP and RTU transports).</summary>
public static class RequestInfo
{
    /// <summary>First address word; 0 when the PDU is too short to have one.</summary>
    public static ushort AddressOf(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 3 ? (ushort)((pdu[1] << 8) | pdu[2]) : (ushort)0;

    /// <summary>Quantity for logging; single-write FCs report 1, unknown FCs 0.</summary>
    public static ushort QuantityOf(ReadOnlySpan<byte> pdu)
    {
        switch ((FunctionCode)pdu[0])
        {
            case FunctionCode.ReadCoils:
            case FunctionCode.ReadDiscreteInputs:
            case FunctionCode.ReadHoldingRegisters:
            case FunctionCode.ReadInputRegisters:
            case FunctionCode.WriteMultipleCoils:
            case FunctionCode.WriteMultipleRegisters:
                return pdu.Length >= 5 ? (ushort)((pdu[3] << 8) | pdu[4]) : (ushort)0;
            case FunctionCode.WriteSingleCoil:
            case FunctionCode.WriteSingleRegister:
                return pdu.Length >= 5 ? (ushort)1 : (ushort)0;
            default:
                return 0;
        }
    }
}