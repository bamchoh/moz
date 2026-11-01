namespace Moz.Devices;

/// <summary>
/// Value types of named variables (karakuri/IEC-ish set). Word types live on
/// word tables, big-endian, high word first. Time types reuse the integer
/// host types (int / uint / ulong); only their meaning differs.
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
    String, // ceil(Length/2) words, UTF-8, first byte in the high byte, NUL padded (host: string)
    Time,   // 2 words, int32 milliseconds (host: int)
    Tod,    // 2 words, uint32 milliseconds since midnight (host: uint)
    Date,   // 4 words, uint64 seconds since the Unix epoch at UTC midnight (host: ulong)
    Dt,     // 4 words, uint64 seconds since the Unix epoch (host: ulong)
    BoolWord, // 1 word, 0 = false / non-zero = true (host: bool); only produced by struct expansion
}

/// <summary>Config spellings of <see cref="VariableType"/> — the single list the
/// config validator, the error messages and the spec table share.</summary>
public static class VariableTypeNames
{
    public static readonly string[] All =
        ["bool", "int", "uint", "dint", "udint", "real", "lint", "ulint", "string", "time", "tod", "date", "dt"];

    public static bool TryParse(string name, out VariableType type)
    {
        switch (name)
        {
            case "bool": type = VariableType.Bool; return true;
            case "int": type = VariableType.Int; return true;
            case "uint": type = VariableType.Uint; return true;
            case "dint": type = VariableType.Dint; return true;
            case "udint": type = VariableType.Udint; return true;
            case "real": type = VariableType.Real; return true;
            case "lint": type = VariableType.Lint; return true;
            case "ulint": type = VariableType.Ulint; return true;
            case "string": type = VariableType.String; return true;
            case "time": type = VariableType.Time; return true;
            case "tod": type = VariableType.Tod; return true;
            case "date": type = VariableType.Date; return true;
            case "dt": type = VariableType.Dt; return true;
            default: type = default; return false;
        }
    }
}

/// <summary>A named, typed overlay range over a device's memory.</summary>
/// <param name="Name">Unique within a device.</param>
/// <param name="Table">Backing memory area.</param>
/// <param name="Offset">0-based cell index of the first element.</param>
/// <param name="Type">Value type of each element.</param>
/// <param name="Count">Number of elements (1 = scalar).</param>
/// <param name="Length">String byte length (1..250); 0 for every other type.</param>
public sealed record Variable(string Name, TableKind Table, int Offset, VariableType Type, int Count = 1, int Length = 0)
{
    public const int MaxStringLength = 250;

    /// <summary>Words per element of a fixed-size word type. Throws for Bool
    /// (bit-sized) and String (sized by <see cref="Length"/>, see <see cref="ElementWords"/>).</summary>
    public static int WordSize(VariableType type) => type switch
    {
        VariableType.Int or VariableType.Uint or VariableType.BoolWord => 1,
        VariableType.Dint or VariableType.Udint or VariableType.Real => 2,
        VariableType.Time or VariableType.Tod => 2,
        VariableType.Lint or VariableType.Ulint => 4,
        VariableType.Date or VariableType.Dt => 4,
        VariableType.Bool => throw new ArgumentException(
            "Bool is bit-sized; use its table kind (Coil/DiscreteInput) instead", nameof(type)),
        VariableType.String => throw new ArgumentException(
            "String is sized by its length; use Variable.ElementWords", nameof(type)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static bool IsBit(VariableType type) => type == VariableType.Bool;

    /// <summary>Words per element (String: ceil(Length / 2)). Throws for Bool.</summary>
    public int ElementWords => Type == VariableType.String ? (Length + 1) / 2 : WordSize(Type);

    /// <summary>Total table cells covered (bits or words).</summary>
    public int Cells => Count * (IsBit(Type) ? 1 : ElementWords);

    public static VariableTypeKind KindOf(VariableType type) =>
        IsBit(type) ? VariableTypeKind.Bit : VariableTypeKind.Word;
}

public enum VariableTypeKind
{
    Bit,
    Word,
}
