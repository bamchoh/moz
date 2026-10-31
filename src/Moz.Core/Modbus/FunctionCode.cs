namespace Moz.Modbus;

/// <summary>Modbus function codes implemented by moz (phase 1 set).</summary>
public enum FunctionCode : byte
{
    ReadCoils = 0x01,
    ReadDiscreteInputs = 0x02,
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
    WriteSingleCoil = 0x05,
    WriteSingleRegister = 0x06,
    WriteMultipleCoils = 0x0F,
    WriteMultipleRegisters = 0x10,
}

/// <summary>Modbus exception codes returned in an exception response (fc | 0x80).</summary>
public enum ExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
}

/// <summary>Spec-imposed request limits (docs/spec-modbus.md is normative).</summary>
public static class ModbusLimits
{
    public const int ReadBitsMax = 2000;
    public const int ReadRegsMax = 125;
    public const int WriteBitsMax = 1968;
    public const int WriteRegsMax = 123;

    /// <summary>Maximum PDU size (function code + data). 253 per spec.</summary>
    public const int PduMaxLength = 253;

    /// <summary>Valid MBAP protocol identifier.</summary>
    public const ushort ProtocolId = 0;

    public const ushort UnitIdMin = 0;
    public const ushort UnitIdMax = 247;
}