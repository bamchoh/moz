namespace Moz.Devices;

/// <summary>
/// One of the four simulated memory tables. Bit tables hold <c>bool</c>,
/// word tables hold <c>ushort</c>. Indexes are 0-based everywhere in moz
/// (the 1-based Modbus addressing convention is a display concern of clients).
/// </summary>
public enum TableKind
{
    Coil,
    DiscreteInput,
    HoldingRegister,
    InputRegister,
}

/// <summary>Helpers usable without knowing which kinds are bits and which are words.</summary>
public static class TableKindExtensions
{
    public static bool IsBit(this TableKind table) =>
        table is TableKind.Coil or TableKind.DiscreteInput;

    public static bool IsWord(this TableKind table) =>
        table is TableKind.HoldingRegister or TableKind.InputRegister;
}