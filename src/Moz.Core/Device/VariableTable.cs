using Microsoft.Extensions.Logging;
using Moz.Logging;

namespace Moz.Devices;

/// <summary>
/// Typed named access over one device. Values are decoded/encoded between
/// the host types (bool, int, uint, float) and the big-endian word layout;
/// every mutation of a variable range is logged as one line, whether it
/// comes from us (scripts, GUI) or from a Modbus master writing raw cells.
/// Values are the observation surface of a future GUI and the OPC UA nodes.
/// </summary>
public sealed class VariableTable
{
    private readonly IDeviceAccess _device;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Variable> _byName = new(StringComparer.Ordinal);

    /// <summary>Throws on programming bugs (config validation reports user errors earlier).</summary>
    public VariableTable(IDeviceAccess device, IEnumerable<Variable> variables, ILogger logger)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        foreach (var v in variables)
        {
            if (_byName.ContainsKey(v.Name))
            {
                throw new KeyNotFoundException($"duplicate variable name \"{v.Name}\"");
            }
            if (Variable.KindOf(v.Type) != TableKindToTypeKind(v.Table))
            {
                throw new ArgumentException(
                    $"variable \"{v.Name}\": type {v.Type} requires a {(Variable.KindOf(v.Type) == VariableTypeKind.Bit ? "bit (coil/discrete)" : "word (holding/input)")} table");
            }
            if (v.Offset < 0 || v.Offset + v.Cells > device.Length(v.Table))
            {
                throw new ArgumentOutOfRangeException(nameof(variables),
                    $"variable \"{v.Name}\" covers [{v.Offset}, {v.Offset + v.Cells}) but {v.Table} has {device.Length(v.Table)} cells");
            }
            _byName.Add(v.Name, v);
        }
        Variables = _byName.Values.ToList().AsReadOnly();

        // change subscription: one watcher per variable, dispatched outside
        // the device lock; the log line re-reads the variable's full range.
        foreach (var v in Variables)
        {
            _device.Watch(v.Table, v.Offset, v.Cells, _ => LogChange(v));
        }
    }

    private static VariableTypeKind TableKindToTypeKind(TableKind table) =>
        table.IsBit() ? VariableTypeKind.Bit : VariableTypeKind.Word;

    public IDeviceAccess Device => _device;

    /// <summary>All variables, name-uniqueness enforced by the constructor.</summary>
    public IReadOnlyList<Variable> Variables { get; }

    /// <summary>Lookup by name; unknown names throw <see cref="KeyNotFoundException"/>.</summary>
    public Variable Find(string name) => _byName[name];

    // --- typed access -------------------------------------------------------

    /// <summary>
    /// Read element <paramref name="index"/> of a variable as one of the
    /// host types: bool, int, uint or float.
    /// </summary>
    public T Read<T>(string name, int index = 0)
    {
        var v = CheckedVar<T>(name);
        CheckIndex(v, index);
        object value = Variable.IsBit(v.Type)
            ? (object)_device.ReadBit(v.Table, v.Offset + index)
            : Decode(v.Type, ReadWords(v, index, Variable.WordSize(v.Type)));
        return (T)value;
    }

    /// <summary>Read all elements of a variable.</summary>
    public T[] ReadAll<T>(string name)
    {
        var v = CheckedVar<T>(name);
        if (Variable.IsBit(v.Type))
        {
            var bits = new bool[v.Count];
            _device.Read(v.Table, v.Offset, bits);
            return bits.Cast<T>().ToArray();
        }
        var words = new ushort[v.Cells];
        _device.Read(v.Table, v.Offset, words);
        var result = new T[v.Count];
        int ws = Variable.WordSize(v.Type);
        for (int i = 0; i < v.Count; i++)
        {
            result[i] = (T)Decode(v.Type, words.AsSpan(i * ws, ws));
        }
        return result;
    }

    /// <summary>Write element <paramref name="index"/> of a variable.</summary>
    public void Write<T>(string name, T value, int index = 0)
    {
        var v = CheckedVar<T>(name);
        CheckIndex(v, index);
        if (Variable.IsBit(v.Type))
        {
            _device.WriteBit(v.Table, v.Offset + index, (bool)(object)value!);
            return;
        }
        var ws = Variable.WordSize(v.Type);
        Span<ushort> words = stackalloc ushort[ws];
        Encode(v.Type, value!, words);
        _device.Write(v.Table, v.Offset + index * ws, words);
    }

    /// <summary>Write all elements (one device call → one change event).</summary>
    public void WriteAll<T>(string name, T[] values)
    {
        var v = CheckedVar<T>(name);
        if (values.Length != v.Count)
        {
            throw new ArgumentException(
                $"variable \"{v.Name}\" has {v.Count} elements, got {values.Length}", nameof(values));
        }
        if (Variable.IsBit(v.Type))
        {
            Span<bool> bits = stackalloc bool[v.Count];
            for (int i = 0; i < v.Count; i++)
            {
                bits[i] = (bool)(object)values[i]!;
            }
            _device.Write(v.Table, v.Offset, bits);
            return;
        }
        int ws = Variable.WordSize(v.Type);
        var words = new ushort[v.Cells];
        for (int i = 0; i < v.Count; i++)
        {
            Encode(v.Type, values[i]!, words.AsSpan(i * ws, ws));
        }
        _device.Write(v.Table, v.Offset, words);
    }

    // --- change logging -----------------------------------------------------

    /// <summary>One log line per mutation batch: "plc-a temperature=12.5" or "plc-a flags=[true,false,true]".</summary>
    internal void LogChange(Variable v)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }
        string value = Variable.IsBit(v.Type)
            ? FormatBitArray(v)
            : FormatWordArray(v);
        _logger.VarChange(_device.Name, v.Name, value);
    }

    private string FormatBitArray(Variable v)
    {
        var bits = new bool[v.Count];
        _device.Read(v.Table, v.Offset, bits);
        return VariableText(v, bits.Select(b => b ? "true" : "false"));
    }

    private string FormatWordArray(Variable v)
    {
        var words = new ushort[v.Cells];
        _device.Read(v.Table, v.Offset, words);
        int ws = Variable.WordSize(v.Type);
        return VariableText(v, Enumerable.Range(0, v.Count)
            .Select(i => DecodeText(v.Type, words.AsSpan(i * ws, ws))));
    }

    private static string VariableText(Variable v, IEnumerable<string> elements) =>
        v.Count == 1
            ? elements.First()
            : "[" + string.Join(',', elements) + "]";

    // --- internals ------------------------------------------------------------

    private Variable CheckedVar<T>(string name)
    {
        var v = Find(name);
        RequireHostType(v, typeof(T));
        return v;
    }

    private static void CheckIndex(Variable v, int index)
    {
        if (index < 0 || index >= v.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index),
                $"variable \"{v.Name}\" has {v.Count} elements, index {index} is out of range");
        }
    }

    private static readonly Type[] HostTypes = [typeof(bool), typeof(int), typeof(uint), typeof(float)];

    private static void RequireHostType(Variable v, Type type)
    {
        if (!HostTypes.Contains(type))
        {
            throw new ArgumentException(
                $"variable \"{v.Name}\" reads/writes one of bool|int|uint|float, got {type.Name}", type.Name);
        }
        // int must not be used against unsigned variables, etc.
        var expected = v.Type switch
        {
            VariableType.Bool => typeof(bool),
            VariableType.Int or VariableType.Dint => typeof(int),
            VariableType.Uint or VariableType.Udint => typeof(uint),
            VariableType.Real => typeof(float),
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Type, null),
        };
        if (type != expected)
        {
            throw new ArgumentException($"variable \"{v.Name}\" is of type {v.Type}; use {expected.Name}");
        }
    }

    /// <summary>Decode big-endian words into the host type (boxed).</summary>
    private static object Decode(VariableType type, ReadOnlySpan<ushort> words)
    {
        switch (type)
        {
            case VariableType.Bool:
                throw new InvalidOperationException("bool decode not via Decode()");
            case VariableType.Int:
                return (int)(short)words[0];
            case VariableType.Uint:
                return (uint)words[0];
            case VariableType.Dint:
                return (int)(((uint)words[0] << 16) | words[1]);
            case VariableType.Udint:
                return ((uint)words[0] << 16) | words[1];
            case VariableType.Real:
                return BitConverter.UInt32BitsToSingle(((uint)words[0] << 16) | words[1]);
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    private static void Encode(VariableType type, object value, Span<ushort> dest)
    {
        // unbox to the exact host type first (int/uint), then narrow
        uint bits = type switch
        {
            VariableType.Int => unchecked((ushort)(int)value),
            VariableType.Uint => (ushort)(uint)value,
            VariableType.Dint => unchecked((uint)(int)value),
            VariableType.Udint => (uint)value,
            VariableType.Real => BitConverter.SingleToUInt32Bits((float)value),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
        if (type is VariableType.Int or VariableType.Uint)
        {
            dest[0] = (ushort)bits;
        }
        else
        {
            dest[0] = (ushort)(bits >> 16);
            dest[1] = (ushort)bits;
        }
    }

    private ushort[] ReadWords(Variable v, int index, int count)
    {
        var words = new ushort[count];
        _device.Read(v.Table, v.Offset + index * count, words);
        return words;
    }

    private static string DecodeText(VariableType type, ReadOnlySpan<ushort> words) =>
        FormatValue(Decode(type, words));

    private static string FormatValue(object value) => value switch
    {
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}