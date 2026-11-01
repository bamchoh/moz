using Moz.Devices;
using Opc.Ua;

namespace Moz.OpcUa;

/// <summary>
/// moz variable ⇄ OPC UA Variant. One place knows the wire type of every
/// <see cref="VariableType"/> (int/uint are 16-bit on the wire, 32-bit on the
/// host) and how arrays (<c>count &gt; 1</c>) map to ValueRank OneDimension.
/// Writes throw: FormatException (Variant type ≠ DataType) → BadTypeMismatch,
/// ArgumentException (range / array length) → BadOutOfRange,
/// InvalidOperationException (read-only table) → BadNotWritable.
/// </summary>
internal static class UaValueCodec
{
    public static NodeId DataType(VariableType type) => type switch
    {
        VariableType.Bool or VariableType.BoolWord => DataTypeIds.Boolean,
        VariableType.Int => DataTypeIds.Int16,
        VariableType.Uint => DataTypeIds.UInt16,
        VariableType.Dint => DataTypeIds.Int32,
        VariableType.Udint => DataTypeIds.UInt32,
        VariableType.Real => DataTypeIds.Float,
        VariableType.Lint => DataTypeIds.Int64,
        VariableType.Ulint => DataTypeIds.UInt64,
        VariableType.String => DataTypeIds.String,
        VariableType.Time => DataTypeIds.Int32,
        VariableType.Tod => DataTypeIds.UInt32,
        VariableType.Date or VariableType.Dt => DataTypeIds.DateTime,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static int ValueRank(Variable v) => v.Count > 1 ? ValueRanks.OneDimension : ValueRanks.Scalar;

    /// <summary>Fixed dimension for arrays; the SDK's default (empty) for scalars.</summary>
    public static ArrayOf<uint> ArrayDimensions(Variable v) =>
        v.Count > 1 ? new ArrayOf<uint>(new[] { (uint)v.Count }) : default;

    /// <summary>Current device value as the node's Variant (scalar or ArrayOf).</summary>
    public static Variant Read(VariableTable table, Variable v) => v.Type switch
    {
        VariableType.Bool or VariableType.BoolWord =>
            Get<bool, bool>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Int => Get<int, short>(table, v, h => (short)h, w => new Variant(w), a => new Variant(a)),
        VariableType.Uint => Get<uint, ushort>(table, v, h => (ushort)h, w => new Variant(w), a => new Variant(a)),
        VariableType.Dint => Get<int, int>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Udint => Get<uint, uint>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Real => Get<float, float>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Lint => Get<long, long>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Ulint => Get<ulong, ulong>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.String => Get<string, string>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Time => Get<int, int>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Tod => Get<uint, uint>(table, v, h => h, w => new Variant(w), a => new Variant(a)),
        VariableType.Date or VariableType.Dt =>
            Get<ulong, DateTimeUtc>(table, v, ToDateTimeUtc, w => new Variant(w), a => new Variant(a)),
        _ => throw new ArgumentOutOfRangeException(nameof(v), v.Type, null),
    };

    /// <summary>Client Variant → device (whole array for count &gt; 1).</summary>
    public static void Write(VariableTable table, Variable v, Variant value)
    {
        switch (v.Type)
        {
            case VariableType.Bool:
            case VariableType.BoolWord:
                Put<bool, bool>(table, v, value, (Variant x, out bool w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<bool> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Int:
                Put<short, int>(table, v, value, (Variant x, out short w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<short> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Uint:
                Put<ushort, uint>(table, v, value, (Variant x, out ushort w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<ushort> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Dint:
                Put<int, int>(table, v, value, (Variant x, out int w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<int> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Udint:
                Put<uint, uint>(table, v, value, (Variant x, out uint w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<uint> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Real:
                Put<float, float>(table, v, value, (Variant x, out float w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<float> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Lint:
                Put<long, long>(table, v, value, (Variant x, out long w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<long> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Ulint:
                Put<ulong, ulong>(table, v, value, (Variant x, out ulong w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<ulong> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.String:
                Put<string, string>(table, v, value, (Variant x, out string w) => x.TryGetValue(out w!),
                    (Variant x, out ArrayOf<string> a) => x.TryGetValue(out a), w => w ?? "");
                break;
            case VariableType.Time:
                Put<int, int>(table, v, value, (Variant x, out int w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<int> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Tod:
                Put<uint, uint>(table, v, value, (Variant x, out uint w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<uint> a) => x.TryGetValue(out a), w => w);
                break;
            case VariableType.Date:
                Put<DateTimeUtc, ulong>(table, v, value, (Variant x, out DateTimeUtc w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<DateTimeUtc> a) => x.TryGetValue(out a), w => ToEpochSeconds(w, v) / 86400 * 86400);
                break;
            case VariableType.Dt:
                Put<DateTimeUtc, ulong>(table, v, value, (Variant x, out DateTimeUtc w) => x.TryGetValue(out w),
                    (Variant x, out ArrayOf<DateTimeUtc> a) => x.TryGetValue(out a), w => ToEpochSeconds(w, v));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(v), v.Type, null);
        }
    }

    // --- time -----------------------------------------------------------------------

    private static readonly ulong MaxEpochSeconds = (ulong)(DateTime.MaxValue - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>Unix seconds → UA DateTime (clamped to year 9999).</summary>
    private static DateTimeUtc ToDateTimeUtc(ulong seconds) =>
        DateTimeUtc.From(DateTime.UnixEpoch.AddSeconds(Math.Min(seconds, MaxEpochSeconds)));

    /// <summary>UA DateTime → whole Unix seconds; before 1970 is out of range.</summary>
    private static ulong ToEpochSeconds(DateTimeUtc value, Variable v)
    {
        double seconds = (value.ToDateTime() - DateTime.UnixEpoch).TotalSeconds;
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"variable \"{v.Name}\": {value} is before the Unix epoch");
        }
        return (ulong)Math.Floor(seconds);
    }

    // --- generic plumbing ------------------------------------------------------

    private delegate bool TryGet<T>(Variant value, out T result);

    private static Variant Get<THost, TWire>(VariableTable table, Variable v, Func<THost, TWire> toWire,
        Func<TWire, Variant> scalar, Func<ArrayOf<TWire>, Variant> array)
    {
        if (v.Count == 1)
        {
            return scalar(toWire(table.Read<THost>(v.Name)));
        }
        var hosts = table.ReadAll<THost>(v.Name);
        var wires = new TWire[hosts.Length];
        for (int i = 0; i < hosts.Length; i++)
        {
            wires[i] = toWire(hosts[i]);
        }
        return array(new ArrayOf<TWire>(wires));
    }

    private static void Put<TWire, THost>(VariableTable table, Variable v, Variant value,
        TryGet<TWire> scalar, TryGet<ArrayOf<TWire>> array, Func<TWire, THost> toHost)
    {
        if (v.Count == 1)
        {
            if (!scalar(value, out var wire))
            {
                throw new FormatException(
                    $"variable \"{v.Name}\" expects {typeof(TWire).Name}, got {value.TypeInfo}");
            }
            table.Write(v.Name, toHost(wire));
            return;
        }
        if (!array(value, out var wires))
        {
            throw new FormatException(
                $"variable \"{v.Name}\" expects {typeof(TWire).Name}[{v.Count}], got {value.TypeInfo}");
        }
        var hosts = new THost[wires.Count];
        for (int i = 0; i < hosts.Length; i++)
        {
            hosts[i] = toHost(wires[i]);
        }
        table.WriteAll(v.Name, hosts); // length mismatch → ArgumentException → BadOutOfRange
    }
}
