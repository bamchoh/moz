using System.Globalization;
using System.Text;

namespace Moz.Devices;

/// <summary>
/// Host value ⇄ big-endian word layout of one variable element, plus the
/// text form used by the change log. Bool never comes here (bit tables).
/// Host types: int (Int, Dint, Time), uint (Uint, Udint, Tod), float (Real),
/// long (Lint), ulong (Ulint, Date, Dt), string (String).
/// </summary>
internal static class WordCodec
{
    public static Type HostType(VariableType type) => type switch
    {
        VariableType.Bool or VariableType.BoolWord => typeof(bool),
        VariableType.Int or VariableType.Dint or VariableType.Time => typeof(int),
        VariableType.Uint or VariableType.Udint or VariableType.Tod => typeof(uint),
        VariableType.Real => typeof(float),
        VariableType.Lint => typeof(long),
        VariableType.Ulint or VariableType.Date or VariableType.Dt => typeof(ulong),
        VariableType.String => typeof(string),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Decode one element (boxed host value).</summary>
    public static object Decode(Variable v, ReadOnlySpan<ushort> words)
    {
        switch (v.Type)
        {
            case VariableType.Bool:
                throw new InvalidOperationException("bool decode not via WordCodec");
            case VariableType.BoolWord:
                return words[0] != 0;
            case VariableType.Int:
                return (int)(short)words[0];
            case VariableType.Uint:
                return (uint)words[0];
            case VariableType.Dint:
            case VariableType.Time:
                return (int)(((uint)words[0] << 16) | words[1]);
            case VariableType.Udint:
            case VariableType.Tod:
                return ((uint)words[0] << 16) | words[1];
            case VariableType.Real:
                return BitConverter.UInt32BitsToSingle(((uint)words[0] << 16) | words[1]);
            case VariableType.Lint:
                return (long)U64(words);
            case VariableType.Ulint:
            case VariableType.Date:
            case VariableType.Dt:
                return U64(words);
            case VariableType.String:
                return DecodeString(v, words);
            default:
                throw new ArgumentOutOfRangeException(nameof(v), v.Type, null);
        }
    }

    /// <summary>Encode one element; <paramref name="value"/> must be the exact host type.</summary>
    public static void Encode(Variable v, object value, Span<ushort> dest)
    {
        switch (v.Type)
        {
            case VariableType.BoolWord:
                dest[0] = (bool)value ? (ushort)1 : (ushort)0;
                break;
            case VariableType.Int:
                dest[0] = unchecked((ushort)(int)value);
                break;
            case VariableType.Uint:
                dest[0] = (ushort)(uint)value;
                break;
            case VariableType.Dint:
            case VariableType.Time:
                U32(unchecked((uint)(int)value), dest);
                break;
            case VariableType.Udint:
            case VariableType.Tod:
                U32((uint)value, dest);
                break;
            case VariableType.Real:
                U32(BitConverter.SingleToUInt32Bits((float)value), dest);
                break;
            case VariableType.Lint:
                U64(unchecked((ulong)(long)value), dest);
                break;
            case VariableType.Ulint:
            case VariableType.Date:
            case VariableType.Dt:
                U64((ulong)value, dest);
                break;
            case VariableType.String:
                EncodeString(v, (string)value, dest);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(v), v.Type, null);
        }
    }

    /// <summary>Change-log text of one element: numbers plain, time/tod in ms,
    /// date/dt ISO-8601 UTC, strings quoted with \" and \\ escaped.</summary>
    public static string Format(Variable v, ReadOnlySpan<ushort> words)
    {
        object value = Decode(v, words);
        return v.Type switch
        {
            VariableType.BoolWord => (bool)value ? "true" : "false",
            VariableType.Real => ((float)value).ToString("R", CultureInfo.InvariantCulture),
            VariableType.Date or VariableType.Dt => FormatEpochSeconds((ulong)value),
            VariableType.String => Quote((string)value),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }

    // --- strings ----------------------------------------------------------------------

    private static string DecodeString(Variable v, ReadOnlySpan<ushort> words)
    {
        Span<byte> bytes = stackalloc byte[v.Length];
        int n = 0;
        for (int i = 0; i < v.Length; i++)
        {
            byte b = (i & 1) == 0 ? (byte)(words[i / 2] >> 8) : (byte)words[i / 2];
            if (b == 0)
            {
                break;
            }
            bytes[n++] = b;
        }
        return Encoding.UTF8.GetString(bytes[..n]); // invalid sequences → U+FFFD
    }

    private static void EncodeString(Variable v, string value, Span<ushort> dest)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > v.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"variable \"{v.Name}\" holds {v.Length} bytes, got {byteCount} (UTF-8)");
        }
        Span<byte> bytes = stackalloc byte[v.Length];
        bytes.Clear();
        Encoding.UTF8.GetBytes(value, bytes);
        for (int i = 0; i < dest.Length; i++)
        {
            byte hi = bytes[i * 2];
            byte lo = i * 2 + 1 < v.Length ? bytes[i * 2 + 1] : (byte)0;
            dest[i] = (ushort)((hi << 8) | lo);
        }
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // --- time ---------------------------------------------------------------------------

    /// <summary>Unix seconds → "2026-11-01T09:30:00Z"; out-of-range values stay numeric.</summary>
    public static string FormatEpochSeconds(ulong seconds)
    {
        const ulong max = 253402300799; // 9999-12-31T23:59:59Z
        return seconds > max
            ? seconds.ToString(CultureInfo.InvariantCulture)
            : DateTime.UnixEpoch.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    // --- word helpers -------------------------------------------------------------------

    private static ulong U64(ReadOnlySpan<ushort> w) =>
        ((ulong)w[0] << 48) | ((ulong)w[1] << 32) | ((ulong)w[2] << 16) | w[3];

    private static void U32(uint bits, Span<ushort> dest)
    {
        dest[0] = (ushort)(bits >> 16);
        dest[1] = (ushort)bits;
    }

    private static void U64(ulong bits, Span<ushort> dest)
    {
        dest[0] = (ushort)(bits >> 48);
        dest[1] = (ushort)(bits >> 32);
        dest[2] = (ushort)(bits >> 16);
        dest[3] = (ushort)bits;
    }
}
