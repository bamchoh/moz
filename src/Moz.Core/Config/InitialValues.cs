using System.Text;
using Moz.Devices;
using Tomlyn;
using Tomlyn.Model;

namespace Moz.Config;

/// <summary>
/// `[[variable]] value = ...`: the TOML value of a declaration becomes host
/// values per expanded leaf (<see cref="Resolve"/>, shared by `moz validate`
/// and startup so both agree) and is seeded into the device before any
/// runner starts (<see cref="Apply"/>; no change notifications).
///
/// Shapes: a scalar declaration takes one TOML scalar, or an array of exactly
/// `count` scalars; a struct declaration takes a table keyed by field name
/// (nested structs as nested tables, field arrays as arrays), or an array of
/// exactly `count` such tables. Fields left out stay 0; unknown fields are
/// errors. Integers must fit the type, strings the byte length; date/dt take a
/// TOML date-time (a local date-time is read as UTC) or integer seconds since
/// the Unix epoch, and date floors to 00:00 UTC.
/// </summary>
public static class InitialValues
{
    /// <summary>Convert one declaration's value; throws FormatException with a message that starts with the offending path ("value", "value.speed", "value[1]").</summary>
    public static IReadOnlyList<(Variable Leaf, object[] Values)> Resolve(string name, int count, object value, IReadOnlyList<Variable> leaves)
    {
        bool isStruct = leaves.Count != 1 || leaves[0].Name != name;
        if (!isStruct)
        {
            return [(leaves[0], ConvertLeaf(leaves[0], value, "value"))];
        }

        if (count > 1)
        {
            if (value is not TomlArray elements || elements.Count != count)
            {
                throw new FormatException($"value expects an array of {count} tables (one per element), got {Show(value)}");
            }
            for (int i = 0; i < elements.Count; i++)
            {
                if (elements[i] is not TomlTable)
                {
                    throw new FormatException($"value[{i}] expects a table of field values, got {Show(elements[i])}");
                }
            }
        }
        else if (value is not TomlTable)
        {
            throw new FormatException($"value expects a table of field values, got {Show(value)}");
        }

        var result = new List<(Variable, object[])>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            var rest = leaf.Name[name.Length..]; // ".speed" | "[1].motor.on"
            if (!TryWalk(value, rest, out var leafValue))
            {
                continue; // field not given: stays 0
            }
            var path = "value" + rest;
            consumed.Add(path);
            result.Add((leaf, ConvertLeaf(leaf, leafValue, path)));
        }
        foreach (var path in TerminalPaths(value, "value"))
        {
            if (!consumed.Contains(path))
            {
                throw new FormatException($"unknown field \"{path}\"");
            }
        }
        return result;
    }

    /// <summary>Seed the resolved values into the device (any table, no notifications).</summary>
    public static void Apply(Device device, IReadOnlyList<(Variable Leaf, object[] Values)> resolved)
    {
        foreach (var (leaf, values) in resolved)
        {
            if (Variable.IsBit(leaf.Type))
            {
                device.Seed(leaf.Table, leaf.Offset, values.Select(v => (bool)v).ToArray());
                continue;
            }
            int stride = leaf.ElementWords;
            var words = new ushort[values.Length * stride];
            for (int i = 0; i < values.Length; i++)
            {
                WordCodec.Encode(leaf, values[i], words.AsSpan(i * stride, stride));
            }
            device.Seed(leaf.Table, leaf.Offset, words);
        }
    }

    // --- walking the TOML value --------------------------------------------------------

    /// <summary>Follow ".key" / "[i]" segments from the declaration's value; false when a step is missing.</summary>
    private static bool TryWalk(object root, string rest, out object? found)
    {
        object? cur = root;
        int pos = 0;
        while (pos < rest.Length)
        {
            if (rest[pos] == '[')
            {
                int close = rest.IndexOf(']', pos);
                int index = int.Parse(rest.AsSpan(pos + 1, close - pos - 1));
                pos = close + 1;
                if (cur is not TomlArray array || index >= array.Count)
                {
                    found = null;
                    return false;
                }
                cur = array[index];
                continue;
            }
            // '.' key
            int next = pos + 1;
            while (next < rest.Length && rest[next] != '.' && rest[next] != '[')
            {
                next++;
            }
            var key = rest[(pos + 1)..next];
            pos = next;
            if (cur is not TomlTable table || !table.TryGetValue(key, out cur))
            {
                found = null;
                return false;
            }
        }
        found = cur;
        return true;
    }

    /// <summary>Every path in the value that holds a scalar or an array of scalars.</summary>
    private static IEnumerable<string> TerminalPaths(object? value, string path)
    {
        switch (value)
        {
            case TomlTable table:
                foreach (var kv in table)
                {
                    foreach (var p in TerminalPaths(kv.Value, $"{path}.{kv.Key}"))
                    {
                        yield return p;
                    }
                }
                break;
            case TomlArray array when array.Count > 0 && array[0] is TomlTable:
                for (int i = 0; i < array.Count; i++)
                {
                    foreach (var p in TerminalPaths(array[i], $"{path}[{i}]"))
                    {
                        yield return p;
                    }
                }
                break;
            default:
                yield return path;
                break;
        }
    }

    // --- conversion -----------------------------------------------------------------------

    private static object[] ConvertLeaf(Variable leaf, object? value, string path)
    {
        if (leaf.Count > 1)
        {
            if (value is not TomlArray array || array.Count != leaf.Count)
            {
                throw new FormatException($"{path} expects an array of {leaf.Count} elements, got {Show(value)}");
            }
            var values = new object[leaf.Count];
            for (int i = 0; i < leaf.Count; i++)
            {
                values[i] = ConvertElement(leaf, array[i], $"{path}[{i}]");
            }
            return values;
        }
        if (value is TomlArray or TomlTable)
        {
            throw new FormatException($"{path} expects a single value, got {Show(value)}");
        }
        return [ConvertElement(leaf, value, path)];
    }

    private static object ConvertElement(Variable leaf, object? v, string where) => leaf.Type switch
    {
        VariableType.Bool or VariableType.BoolWord => v is bool b ? b
            : throw new FormatException($"{where} expects true/false, got {Show(v)}"),
        VariableType.Int => (int)Integer(v, where, short.MinValue, short.MaxValue),
        VariableType.Dint or VariableType.Time => (int)Integer(v, where, int.MinValue, int.MaxValue),
        VariableType.Uint => (uint)Integer(v, where, 0, ushort.MaxValue),
        VariableType.Udint or VariableType.Tod => (uint)Integer(v, where, 0, uint.MaxValue),
        VariableType.Lint => Integer(v, where, long.MinValue, long.MaxValue),
        VariableType.Ulint => (ulong)Integer(v, where, 0, long.MaxValue),
        VariableType.Real => v switch
        {
            double d => (float)d,
            long l => (float)l,
            _ => throw new FormatException($"{where} expects a number, got {Show(v)}"),
        },
        VariableType.String => String(leaf, v, where),
        VariableType.Date => EpochSeconds(v, where, floorToDay: true),
        VariableType.Dt => EpochSeconds(v, where, floorToDay: false),
        _ => throw new ArgumentOutOfRangeException(nameof(leaf), leaf.Type, null),
    };

    private static long Integer(object? v, string where, long min, long max)
    {
        if (v is not long l)
        {
            throw new FormatException($"{where} expects an integer in {min}..{max}, got {Show(v)}");
        }
        if (l < min || l > max)
        {
            throw new FormatException($"{where} expects an integer in {min}..{max}, got {l}");
        }
        return l;
    }

    private static string String(Variable leaf, object? v, string where)
    {
        if (v is not string s)
        {
            throw new FormatException($"{where} expects a string, got {Show(v)}");
        }
        int bytes = Encoding.UTF8.GetByteCount(s);
        if (bytes > leaf.Length)
        {
            throw new FormatException($"{where} exceeds length {leaf.Length} ({bytes} bytes)");
        }
        return s;
    }

    private static ulong EpochSeconds(object? v, string where, bool floorToDay)
    {
        long seconds;
        switch (v)
        {
            case TomlDateTime t when t.Kind != TomlDateTimeKind.LocalTime:
                var dto = t.DateTime;
                if (t.Kind is TomlDateTimeKind.LocalDateTime or TomlDateTimeKind.LocalDate)
                {
                    dto = new DateTimeOffset(dto.DateTime, TimeSpan.Zero); // local clock time read as UTC
                }
                seconds = dto.ToUnixTimeSeconds();
                if (seconds < 0)
                {
                    throw new FormatException($"{where} expects a date-time at or after 1970-01-01T00:00:00Z, got {Show(v)}");
                }
                break;
            case long l:
                seconds = l;
                if (seconds < 0)
                {
                    throw new FormatException($"{where} expects seconds since the Unix epoch in 0..{long.MaxValue}, got {l}");
                }
                break;
            default:
                throw new FormatException($"{where} expects a date-time (2026-11-01T09:00:00Z) or integer seconds, got {Show(v)}");
        }
        if (floorToDay)
        {
            seconds -= seconds % 86400;
        }
        return (ulong)seconds;
    }

    private static string Show(object? v) => v switch
    {
        null => "nothing",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        TomlArray => "an array",
        TomlTable => "a table",
        TomlDateTime t => t.ToString(),
        _ => v.ToString() ?? v.GetType().Name,
    };
}
