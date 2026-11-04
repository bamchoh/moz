namespace Moz.Devices;

/// <summary>
/// One of the four simulated memory tables, named without any protocol's
/// vocabulary: Bits / Words are read-write, the ReadOnly ones can only be
/// changed from inside the process (scripts, variables, client mirrors).
/// Bit tables hold <c>bool</c>, word tables hold <c>ushort</c>. Indexes are 0-based everywhere in moz
/// (the 1-based Modbus addressing convention is a display concern of clients).
/// </summary>
public enum TableKind
{
    Bits,
    ReadOnlyBits,
    Words,
    ReadOnlyWords,
}

/// <summary>Helpers usable without knowing which kinds are bits and which are words.</summary>
public static class TableKindExtensions
{
    public static bool IsBit(this TableKind table) =>
        table is TableKind.Bits or TableKind.ReadOnlyBits;

    public static bool IsWord(this TableKind table) =>
        table is TableKind.Words or TableKind.ReadOnlyWords;
}