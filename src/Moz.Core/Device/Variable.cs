namespace Moz.Devices;

/// <summary>
/// Value types of named variables (v1 subset of the karakuri/IEC-ish set).
/// Word types live on word tables, big-endian. Struct / STRING / time types
/// arrive with the OPC UA phase.
/// </summary>
public enum VariableType
{
    Bool,   // 1 bit, bit tables only
    Int,    // 1 word, signed
    Uint,   // 1 word, unsigned
    Dint,   // 2 words, signed, high word first
    Udint,  // 2 words, unsigned, high word first
    Real,   // 2 words, IEEE754 single, big-endian, high word first
    Lint,   // 4 words, signed 64-bit, high word first (script layer exposes it as JS BigInt)
    Ulint,  // 4 words, unsigned 64-bit, high word first
}

/// <summary>A named, typed overlay range over a device's memory.</summary>
/// <param name="Name">Unique within a device.</param>
/// <param name="Table">Backing memory area.</param>
/// <param name="Offset">0-based cell index of the first element.</param>
/// <param name="Type">Value type of each element.</param>
/// <param name="Count">Number of elements (1 = scalar).</param>
public sealed record Variable(string Name, TableKind Table, int Offset, VariableType Type, int Count = 1)
{
    /// <summary>Words per element. Throws for Bool (bit-sized, not word-sized).</summary>
    public static int WordSize(VariableType type) => type switch
    {
        VariableType.Int or VariableType.Uint => 1,
        VariableType.Dint or VariableType.Udint or VariableType.Real => 2,
        VariableType.Lint or VariableType.Ulint => 4,
        VariableType.Bool => throw new ArgumentException(
            "Bool is bit-sized; use its table kind (Coil/DiscreteInput) instead", nameof(type)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static bool IsBit(VariableType type) => type == VariableType.Bool;

    /// <summary>Total table cells covered (bits or words).</summary>
    public int Cells => Count * (IsBit(Type) ? 1 : WordSize(Type));

    public static VariableTypeKind KindOf(VariableType type) =>
        IsBit(type) ? VariableTypeKind.Bit : VariableTypeKind.Word;
}

public enum VariableTypeKind
{
    Bit,
    Word,
}