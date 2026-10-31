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
        var errors = new List<ConfigError>();
        MozConfig? config;
        try
        {
            var text = File.ReadAllText(path);
            config = TomlSerializer.Deserialize<MozConfig>(text);
            if (config is null)
            {
                errors.Add(new ConfigError(path, "file is empty"));
            }
        }
        catch (TomlException ex)
        {
            return new ConfigResult(path, null, [new ConfigError(path, $"TOML parse error: {ex.Message}")]);
        }
        catch (IOException ex)
        {
            return new ConfigResult(path, null, [new ConfigError(path, $"cannot read file: {ex.Message}")]);
        }

        if (config is not null)
        {
            FillDefaults(config);
            Validate(config, path, errors);
        }
        return new ConfigResult(path, Ok(config, errors) ? config : null, errors);
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