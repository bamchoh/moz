using Microsoft.Extensions.Logging;
using Moz.Logging;

namespace Moz.Devices;

/// <summary>
/// Typed named access over one device. Values are decoded/encoded between
/// the host types (bool, int, uint, float, long, ulong, string — see
/// <see cref="WordCodec"/>) and the big-endian word layout; every mutation
/// of a variable range is logged as one line,
/// whether it comes from us (scripts, OPC UA) or from a Modbus master
/// writing raw cells. Scripts and the OPC UA nodes consume this surface.
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
                    $"variable \"{v.Name}\": type {v.Type} requires a {(Variable.KindOf(v.Type) == VariableTypeKind.Bit ? "bit (bits/read_only_bits)" : "word (words/read_only_words)")} table");
            }
            if (v.Type == VariableType.String && (v.Length < 1 || v.Length > Variable.MaxStringLength))
            {
                throw new ArgumentOutOfRangeException(nameof(variables),
                    $"variable \"{v.Name}\": string length {v.Length} is out of range 1..{Variable.MaxStringLength}");
            }
            if (v.Type != VariableType.String && v.Length != 0)
            {
                throw new ArgumentException($"variable \"{v.Name}\": length applies to string only");
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
    /// Read element <paramref name="index"/> of a variable as its host type
    /// (bool, int, uint, float, long, ulong or string).
    /// </summary>
    public T Read<T>(string name, int index = 0)
    {
        var v = CheckedVar<T>(name);
        CheckIndex(v, index);
        object value = Variable.IsBit(v.Type)
            ? (object)_device.ReadBit(v.Table, v.Offset + index)
            : WordCodec.Decode(v, ReadWords(v, index, v.ElementWords));
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
        int ws = v.ElementWords;
        for (int i = 0; i < v.Count; i++)
        {
            result[i] = (T)WordCodec.Decode(v, words.AsSpan(i * ws, ws));
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
        var ws = v.ElementWords;
        Span<ushort> words = stackalloc ushort[ws];
        WordCodec.Encode(v, value!, words);
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
        int ws = v.ElementWords;
        var words = new ushort[v.Cells];
        for (int i = 0; i < v.Count; i++)
        {
            WordCodec.Encode(v, values[i]!, words.AsSpan(i * ws, ws));
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
        int ws = v.ElementWords;
        return VariableText(v, Enumerable.Range(0, v.Count)
            .Select(i => WordCodec.Format(v, words.AsSpan(i * ws, ws))));
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

    private static void RequireHostType(Variable v, Type type)
    {
        var expected = WordCodec.HostType(v.Type);
        if (type != expected)
        {
            throw new ArgumentException($"variable \"{v.Name}\" is of type {v.Type}; use {expected.Name}");
        }
    }

    private ushort[] ReadWords(Variable v, int index, int count)
    {
        var words = new ushort[count];
        _device.Read(v.Table, v.Offset + index * count, words);
        return words;
    }
}
