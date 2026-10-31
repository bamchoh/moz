using Moz.Devices;
using Tomlyn;
using Tomlyn.Serialization;

namespace Moz.Config;

/// <summary>
/// Loads moz.toml strictly: parse errors, unknown keys (extension-data
/// capture), range errors, and semantic errors (duplicate names, unresolvable
/// device references) are all collected and reported together — never fail
/// on the first problem. <see cref="ConfigLoader.Validate"/> is the single
/// validation code path; the future `moz validate` command reuses it.
/// </summary>
public static class ConfigLoader
{
    public static ConfigResult Load(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return LoadText(text, path);
        }
        catch (IOException ex)
        {
            return new ConfigResult(path, null, [new ConfigError(path, $"cannot read file: {ex.Message}")]);
        }
    }

    /// <summary>Parse + validate from TOML text directly (tests and future tooling).</summary>
    public static ConfigResult LoadText(string toml, string sourceName)
    {
        var errors = new List<ConfigError>();
        MozConfig? config;
        try
        {
            config = TomlSerializer.Deserialize<MozConfig>(toml);
            if (config is null)
            {
                errors.Add(new ConfigError(sourceName, "file is empty"));
            }
        }
        catch (TomlException ex)
        {
            return new ConfigResult(sourceName, null, [new ConfigError(sourceName, $"TOML parse error: {ex.Message}")]);
        }

        if (config is not null)
        {
            FillDefaults(config);
            Validate(config, sourceName, errors);
        }
        return new ConfigResult(sourceName, Ok(config, errors) ? config : null, errors);
    }

    /// <summary>
    /// Spec defaults applied in one place (docs/spec-config.md). The other
    /// defaults (log level/format, table sizes, port) are model property
    /// initializers in ConfigModel; unknown values are reported by Validate.
    /// </summary>
    private static void FillDefaults(MozConfig config)
    {
        config.Log ??= new LogConfig();
        foreach (var server in config.Server)
        {
            server.Bind = string.IsNullOrWhiteSpace(server.Bind) ? "127.0.0.1" : server.Bind;
        }
    }

    public static string DefaultConfigHint() =>
        "expected a [log] table, [[device]] tables, and [[server]] tables (see docs/spec-config.md)";

    // --- validation --------------------------------------------------------

    private static bool Ok(MozConfig? config, List<ConfigError> errors) =>
        config is not null && errors.Count == 0;

    private static readonly string[] LogLevels = ["trace", "debug", "info", "warn", "error"];
    private static readonly string[] LogFormats = ["text", "json"];
    private static readonly string[] Protocols = ["modbus-tcp"];

    public static void Validate(MozConfig config, string path, List<ConfigError> errors)
    {
        if (config.Log is not null)
        {
            CheckExtra(config.Log.Extra, "[log]", path, errors);
            CheckChoice(config.Log.Level, LogLevels, "log.level", path, errors, defaultIs: "info");
            CheckChoice(config.Log.Format, LogFormats, "log.format", path, errors, defaultIs: "text");
        }

        CheckExtra(config.Extra, "<root>", path, errors);

        foreach (var device in config.Device)
        {
            Require(device.Name, $"device.name", path, errors, $"[[device]]");
            CheckExtra(device.Extra, $"[device \"{device.Name}\"]", path, errors);
            CheckRange(device.Coils, "device.coils", 1, 65536, path, errors);
            CheckRange(device.Discrete, "device.discrete", 1, 65536, path, errors);
            CheckRange(device.Holding, "device.holding", 1, 65536, path, errors);
            CheckRange(device.Input, "device.input", 1, 65536, path, errors);
        }

        ValidateVariables(config, path, errors);
        CheckVariableOverlaps(config, path, errors);

        foreach (var server in config.Server)
        {
            Require(server.Name, "server.name", path, errors, "[[server]]");
            Require(server.Device, "server.device", path, errors, $"[server \"{server.Name}\"]");
            CheckExtra(server.Extra, $"[server \"{server.Name}\"]", path, errors);
            CheckChoice(server.Protocol, Protocols, "server.protocol", path, errors,
                defaultIs: null, context: $"[server \"{server.Name}\"]");
            CheckRange(server.Port, "server.port", 0, 65535, path, errors);
            if (server.UnitIds is { Count: > 0 })
            {
                foreach (var unit in server.UnitIds)
                {
                    if (unit is < 0 or > Modbus.ModbusLimits.UnitIdMax)
                    {
                        errors.Add(new ConfigError(path,
                            $"server.unit_ids value {unit} of \"{server.Name}\" is out of range 0..{Modbus.ModbusLimits.UnitIdMax}"));
                    }
                }
            }
        }

        // duplicate names: devices and servers are separate namespaces,
        // but each must be internally unique
        CheckDuplicates(config.Device.Select(d => d.Name), "device", path, errors);
        CheckDuplicates(config.Server.Select(s => s.Name), "server", path, errors);

        // device references resolve
        var known = config.Device.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var server in config.Server)
        {
            if (server.Device.Length > 0 && !known.Contains(server.Device))
            {
                errors.Add(new ConfigError(path,
                    $"[server \"{server.Name}\"] references device \"{server.Device}\" which is not declared"));
            }
        }
    }

    // --- variable validation ------------------------------------------------

    private static readonly string[] VariableKinds = ["coil", "discrete", "holding", "input"];
    private static readonly string[] VariableTypes = ["bool", "int", "uint", "dint", "udint", "real"];

    /// <summary>
    /// Per-variable checks (name, device reference, kind/type/compatibility,
    /// count) then a layout pass per (device, table): range vs device size and
    /// range overlap. All problems are reported together.
    /// </summary>
    private static void ValidateVariables(MozConfig config, string path, List<ConfigError> errors)
    {
        var devices = config.Device.ToDictionary(d => d.Name, StringComparer.Ordinal);
        var nameSeen = new HashSet<string>(StringComparer.Ordinal); // device + name uniqueness

        foreach (var variable in config.Variable)
        {
            var devName = variable.Device.Length > 0 ? variable.Device : "<no device>";
            var ctx = $"[[variable]] \"{variable.Name}\" on device \"{devName}\"";
            Require(variable.Name, "variable.name", path, errors, ctx);
            Require(variable.Device, "variable.device", path, errors, ctx);
            CheckExtra(variable.Extra, ctx, path, errors);
            CheckChoice(variable.Kind, VariableKinds, "variable.kind", path, errors, defaultIs: null, context: ctx);
            CheckChoice(variable.Type, VariableTypes, "variable.type", path, errors, defaultIs: null, context: ctx);
            if (!TryTableKind(variable.Kind, out var table) || !TryVariableType(variable.Type, out var type))
            {
                continue; // enum values already reported
            }
            bool bitType = Variable.KindOf(type) == VariableTypeKind.Bit;
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
            if (!devices.TryGetValue(variable.Device, out var device))
            {
                errors.Add(new ConfigError(path,
                    $"[[variable]] \"{variable.Name}\" references device \"{variable.Device}\" which is not declared"));
                continue;
            }
            if (variable.Name.Length > 0 && !nameSeen.Add(variable.Device + " " + variable.Name))
            {
                errors.Add(new ConfigError(path,
                    $"duplicate variable name \"{variable.Name}\" on device \"{device.Name}\""));
            }
            CheckVariableRange(variable, device, bitType, table, type, path, errors);
        }
    }

    /// <summary>Range vs device length and overlap within one (device, table).</summary>
    private static void CheckVariableRange(VariableConfig variable, DeviceConfig device,
        bool bitType, TableKind table, VariableType type, string path, List<ConfigError> errors)
    {
        int length = table switch
        {
            TableKind.Coil => device.Coils,
            TableKind.DiscreteInput => device.Discrete,
            TableKind.HoldingRegister => device.Holding,
            TableKind.InputRegister => device.Input,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };
        int cells = bitType ? variable.Count : Variable.WordSize(type) * variable.Count;
        if (variable.Offset < 0 || variable.Offset + cells > length)
        {
            errors.Add(new ConfigError(path,
                $"variable \"{variable.Name}\" at kind \"{variable.Kind}\" offset {variable.Offset} (+{cells} cells) " +
                $"exceeds device \"{device.Name}\" length {length}"));
        }
    }

    /// <summary>Overlap between variables sharing a device table (reported once per pair).</summary>
    private static void CheckVariableOverlaps(MozConfig config, string path, List<ConfigError> errors)
    {
        var ranges = new Dictionary<(string Device, string Kind), List<(string Name, int Start, int End)>>();
        foreach (var variable in config.Variable)
        {
            if (!TryTableKind(variable.Kind, out var table) || !TryVariableType(variable.Type, out var type))
            {
                continue;
            }
            int cells = Variable.KindOf(type) == VariableTypeKind.Bit
                ? variable.Count
                : Variable.WordSize(type) * variable.Count;
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

    private static bool TryVariableType(string type, out VariableType parsed)
    {
        switch (type)
        {
            case "bool": parsed = VariableType.Bool; return true;
            case "int": parsed = VariableType.Int; return true;
            case "uint": parsed = VariableType.Uint; return true;
            case "dint": parsed = VariableType.Dint; return true;
            case "udint": parsed = VariableType.Udint; return true;
            case "real": parsed = VariableType.Real; return true;
            default: parsed = default; return false;
        }
    }

    private static void Require(string value, string key, string path, List<ConfigError> errors, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ConfigError(path, $"{key} is required ({context})"));
        }
    }

    private static void CheckChoice(string? value, string[] choices, string key, string path,
        List<ConfigError> errors, string? defaultIs, string? context = null)
    {
        if (value is null)
        {
            return; // default applies
        }
        if (!choices.Contains(value))
        {
            errors.Add(new ConfigError(path,
                $"{key} value \"{value}\" is invalid ({string.Join('|', choices)}" +
                (defaultIs is null ? ")" : $"; default \"{defaultIs}\")")));
        }
    }

    private static void CheckRange(int value, string key, int min, int max, string path, List<ConfigError> errors)
    {
        if (value < min || value > max)
        {
            errors.Add(new ConfigError(path, $"{key} value {value} is out of range {min}..{max}"));
        }
    }

    private static void CheckDuplicates(IEnumerable<string> names, string kind, string path, List<ConfigError> errors)
    {
        foreach (var group in names.Where(n => n.Length > 0).GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add(new ConfigError(path, $"duplicate {kind} name \"{group.Key}\""));
        }
    }

    /// <summary>The strict unknown-key detector: Tomlyn records unmapped keys here.</summary>
    private static void CheckExtra(IDictionary<string, object?>? extra, string context, string path, List<ConfigError> errors)
    {
        if (extra is null)
        {
            return;
        }
        foreach (var key in extra.Keys)
        {
            errors.Add(new ConfigError(path, $"unknown key '{key}' under {context}"));
        }
    }
}