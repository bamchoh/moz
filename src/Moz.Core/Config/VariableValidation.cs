using Moz.Devices;

namespace Moz.Config;

/// <summary>
/// [[struct]] and [[variable]] validation. Structs: names, fields, resolvable
/// field types, string lengths, cycles. Variables: per-declaration checks
/// (name, device reference, kind/type compatibility, count, string length),
/// then — through the same <see cref="VariableLayout.Expand"/> the runtime
/// uses — leaf-name uniqueness, the range check against the device size and
/// the overlap check per (device, table). Validator and runtime therefore
/// never disagree about a declaration's footprint.
/// </summary>
public static partial class ConfigLoader
{
    private static readonly string[] VariableKinds = ["coil", "discrete", "holding", "input"];

    /// <summary>Struct definitions as the runtime consumes them (first declaration wins on duplicates).</summary>
    public static IReadOnlyDictionary<string, StructDef> ToStructDefs(MozConfig config)
    {
        var defs = new Dictionary<string, StructDef>(StringComparer.Ordinal);
        foreach (var s in config.Struct)
        {
            if (s.Name.Length == 0 || defs.ContainsKey(s.Name))
            {
                continue;
            }
            defs[s.Name] = new StructDef(s.Name,
                s.Field.Select(f => new StructField(f.Name, f.Type, f.Count, f.Length ?? 0)).ToList());
        }
        return defs;
    }

    // --- [[struct]] --------------------------------------------------------------

    private static void ValidateStructs(MozConfig config, string path, List<ConfigError> errors)
    {
        var declared = config.Struct.Select(s => s.Name).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var fieldTypes = VariableTypeNames.All.Concat(declared).ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in config.Struct)
        {
            var ctx = $"[[struct]] \"{s.Name}\"";
            Require(s.Name, "struct.name", path, errors, ctx);
            CheckExtra(s.Extra, ctx, path, errors);
            if (VariableTypeNames.TryParse(s.Name, out _))
            {
                errors.Add(new ConfigError(path, $"struct name \"{s.Name}\" collides with a built-in type"));
            }
            if (s.Name.Length > 0 && !names.Add(s.Name))
            {
                errors.Add(new ConfigError(path, $"duplicate struct name \"{s.Name}\""));
            }
            if (s.Field.Count == 0)
            {
                errors.Add(new ConfigError(path, $"{ctx} has no fields"));
            }
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in s.Field)
            {
                var fctx = $"{ctx} field \"{f.Name}\"";
                Require(f.Name, "struct.field.name", path, errors, fctx);
                CheckExtra(f.Extra, fctx, path, errors);
                if (f.Name.Length > 0 && !fieldNames.Add(f.Name))
                {
                    errors.Add(new ConfigError(path, $"duplicate field name \"{f.Name}\" in {ctx}"));
                }
                CheckChoice(f.Type, fieldTypes, "struct.field.type", path, errors, defaultIs: null, context: fctx);
                if (f.Count < 1)
                {
                    errors.Add(new ConfigError(path, $"struct.field.count value {f.Count} is out of range 1.. in {fctx}"));
                }
                CheckLength(f.Type, f.Length, "struct.field.length", fctx, path, errors);
            }
        }

        // cycles: Words() walks the definition graph and throws on re-entry
        var defs = ToStructDefs(config);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in defs.Keys)
        {
            try
            {
                VariableLayout.Words(name, 0, defs);
            }
            catch (ArgumentException ex) when (ex.Message.StartsWith("struct cycle", StringComparison.Ordinal))
            {
                if (reported.Add(ex.Message))
                {
                    errors.Add(new ConfigError(path, ex.Message));
                }
            }
            catch (ArgumentException)
            {
                // unknown field type: reported per field above
            }
        }
    }

    // --- [[variable]] --------------------------------------------------------------

    /// <summary>
    /// Per-variable checks, then the expanded leaves: name uniqueness per
    /// device, range vs device size and overlap per (device, table). All
    /// problems are reported together.
    /// </summary>
    private static void ValidateVariables(MozConfig config, string path, List<ConfigError> errors)
    {
        var devices = config.Device.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var structs = ToStructDefs(config);
        var typeNames = VariableTypeNames.All.Concat(structs.Keys).ToArray();
        var nameSeen = new HashSet<(string Device, string Name)>(); // leaf name uniqueness per device
        var ranges = new Dictionary<(string Device, string Kind), List<(string Name, int Start, int End)>>();

        foreach (var variable in config.Variable)
        {
            var devName = variable.Device.Length > 0 ? variable.Device : "<no device>";
            var ctx = $"[[variable]] \"{variable.Name}\" on device \"{devName}\"";
            Require(variable.Name, "variable.name", path, errors, ctx);
            Require(variable.Device, "variable.device", path, errors, ctx);
            CheckExtra(variable.Extra, ctx, path, errors);
            CheckChoice(variable.Kind, VariableKinds, "variable.kind", path, errors, defaultIs: null, context: ctx);
            CheckChoice(variable.Type, typeNames, "variable.type", path, errors, defaultIs: null, context: ctx);
            bool isStruct = structs.ContainsKey(variable.Type);
            bool isScalar = VariableTypeNames.TryParse(variable.Type, out var scalar);
            if (!TryTableKind(variable.Kind, out var table) || !(isStruct || isScalar))
            {
                continue; // enum values already reported
            }
            bool bitType = isScalar && Variable.KindOf(scalar) == VariableTypeKind.Bit;
            if (bitType != table.IsBit())
            {
                errors.Add(new ConfigError(path, bitType
                    ? $"type \"{variable.Type}\" requires kind coil|discrete (got \"{variable.Kind}\") in {ctx}"
                    : $"type \"{variable.Type}\" requires kind holding|input (got \"{variable.Kind}\") in {ctx}"));
            }
            if (variable.Count < 1)
            {
                errors.Add(new ConfigError(path,
                    $"variable.count value {variable.Count} is out of range 1.. in {ctx}"));
            }
            CheckLength(variable.Type, variable.Length, "variable.length", ctx, path, errors);
            if (!devices.TryGetValue(variable.Device, out var device))
            {
                errors.Add(new ConfigError(path,
                    $"[[variable]] \"{variable.Name}\" references device \"{variable.Device}\" which is not declared"));
                continue;
            }

            var leaves = ExpandForValidation(variable, table, structs);
            foreach (var leaf in leaves)
            {
                if (leaf.Name.Length > 0 && !nameSeen.Add((variable.Device, leaf.Name)))
                {
                    errors.Add(new ConfigError(path,
                        $"duplicate variable name \"{leaf.Name}\" on device \"{device.Name}\""));
                }
            }
            int cells = leaves.Sum(l => l.Cells);
            CheckVariableRange(variable, device, table, cells, path, errors);
            CheckVariableOverlap(variable, cells, ranges, path, errors);
        }
    }

    /// <summary>Leaves of one declaration; count/length clamped so already-reported
    /// range errors cannot make the expansion itself fail.</summary>
    private static IReadOnlyList<Variable> ExpandForValidation(VariableConfig variable, TableKind table,
        IReadOnlyDictionary<string, StructDef> structs)
    {
        int count = Math.Max(1, variable.Count);
        int length = Math.Clamp(variable.Length ?? 0, 0, Variable.MaxStringLength);
        try
        {
            return VariableLayout.Expand(variable.Name, table, variable.Offset, variable.Type, count, length, structs);
        }
        catch (ArgumentException)
        {
            return []; // unknown type / cycle: reported by ValidateStructs
        }
    }

    /// <summary>Range vs device length.</summary>
    private static void CheckVariableRange(VariableConfig variable, DeviceConfig device,
        TableKind table, int cells, string path, List<ConfigError> errors)
    {
        int length = table switch
        {
            TableKind.Coil => device.Coils,
            TableKind.DiscreteInput => device.Discrete,
            TableKind.HoldingRegister => device.Holding,
            TableKind.InputRegister => device.Input,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };
        if (variable.Offset < 0 || variable.Offset + cells > length)
        {
            errors.Add(new ConfigError(path,
                $"variable \"{variable.Name}\" at kind \"{variable.Kind}\" offset {variable.Offset} (+{cells} cells) " +
                $"exceeds device \"{device.Name}\" length {length}"));
        }
    }

    /// <summary>Overlap between declarations sharing a device table (reported once per pair).</summary>
    private static void CheckVariableOverlap(VariableConfig variable, int cells,
        Dictionary<(string Device, string Kind), List<(string Name, int Start, int End)>> ranges,
        string path, List<ConfigError> errors)
    {
        var key = (variable.Device, variable.Kind);
        var list = ranges.TryGetValue(key, out var l) ? l : ranges[key] = [];
        foreach (var (otherName, otherStart, otherEnd) in list)
        {
            if (variable.Offset < otherEnd && otherStart < variable.Offset + cells)
            {
                errors.Add(new ConfigError(path,
                    $"variable \"{variable.Name}\" (kind \"{variable.Kind}\", offset {variable.Offset}) overlaps " +
                    $"\"{otherName}\" (offset {otherStart}, {otherEnd - otherStart} cells) on device \"{variable.Device}\""));
            }
        }
        list.Add((variable.Name, variable.Offset, variable.Offset + cells));
    }

    /// <summary>string needs length 1..250; every other type must not set it.</summary>
    private static void CheckLength(string type, int? length, string key, string ctx, string path, List<ConfigError> errors)
    {
        if (type == "string")
        {
            if (length is null)
            {
                errors.Add(new ConfigError(path, $"{key} is required for type \"string\" in {ctx}"));
            }
            else
            {
                CheckRange(length.Value, key, 1, Variable.MaxStringLength, path, errors);
            }
        }
        else if (length is not null)
        {
            errors.Add(new ConfigError(path,
                $"{key} is only valid for type \"string\" (got type \"{type}\") in {ctx}"));
        }
    }

    private static bool TryTableKind(string kind, out TableKind table)
    {
        switch (kind)
        {
            case "coil": table = TableKind.Coil; return true;
            case "discrete": table = TableKind.DiscreteInput; return true;
            case "holding": table = TableKind.HoldingRegister; return true;
            case "input": table = TableKind.InputRegister; return true;
            default: table = default; return false;
        }
    }
}
