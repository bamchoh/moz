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
public static partial class ConfigLoader
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
        foreach (var client in config.Client)
        {
            foreach (var poll in client.Poll)
            {
                poll.LocalKind ??= PollConfig.DefaultLocalKind(poll.Fc);
            }
            foreach (var push in client.Push)
            {
                push.LocalKind ??= PushConfig.DefaultLocalKind(push.Fc);
            }
        }
    }

    public static string DefaultConfigHint() =>
        "expected a [log] table, [[device]] tables, and [[server]] tables (see docs/spec-config.md)";

    // --- validation --------------------------------------------------------

    private static bool Ok(MozConfig? config, List<ConfigError> errors) =>
        config is not null && errors.Count == 0;

    private static readonly string[] LogLevels = ["trace", "debug", "info", "warn", "error"];
    private static readonly string[] LogFormats = ["text", "json"];
    private static readonly string[] Protocols = ["modbus-tcp", "modbus-rtu", "opcua"];

    private static void Validate(MozConfig config, string path, List<ConfigError> errors)
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

        ValidateStructs(config, path, errors);
        ValidateVariables(config, path, errors);
        ValidateClients(config, path, errors);

        foreach (var script in config.Script)
        {
            Require(script.Name, "script.name", path, errors, "[[script]]");
            Require(script.Device, "script.device", path, errors, $"[script \"{script.Name}\"]");
            Require(script.Path, "script.path", path, errors, $"[script \"{script.Name}\"]");
            CheckExtra(script.Extra, $"[script \"{script.Name}\"]", path, errors);
            CheckRange(script.IntervalMs, "script.interval_ms", 100, 3_600_000, path, errors);
        }
        CheckDuplicates(config.Script.Select(s => s.Name), "script", path, errors);
        CheckDuplicates(config.Client.Select(c => c.Name), "client", path, errors);

        // script device references resolve
        var deviceNames = config.Device.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var script in config.Script)
        {
            if (script.Device.Length > 0 && !deviceNames.Contains(script.Device))
            {
                errors.Add(new ConfigError(path,
                    $"[script \"{script.Name}\"] references device \"{script.Device}\" which is not declared"));
            }
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

            // serial-protocol wiring (phase 5)
            bool serialProtocol = server.Protocol == "modbus-rtu";
            if (serialProtocol)
            {
                Require(server.SerialPort ?? "", "server.serial_port", path, errors,
                    $"[server \"{server.Name}\"] (required for modbus-rtu)");
            }
            else if (server.SerialPort is not null)
            {
                errors.Add(new ConfigError(path,
                    $"server.serial_port is only valid for serial protocols (modbus-rtu), set on \"{server.Name}\" ({server.Protocol})"));
            }

            // opcua wiring (phase 7)
            if (server.Protocol == "opcua")
            {
                Require(server.Endpoint ?? "", "server.endpoint", path, errors,
                    $"[server \"{server.Name}\"] (required for opcua)");
                if (server.Endpoint is { } endpointValue)
                {
                    if (!TryParseOpcUaEndpoint(endpointValue))
                    {
                        errors.Add(new ConfigError(path,
                            $"server.endpoint value \"{endpointValue}\" is invalid (opc.tcp://host:port, port 1..65535) on \"{server.Name}\""));
                    }
                    if (server.SerialPort is not null)
                    {
                        errors.Add(new ConfigError(path,
                            $"server.serial_port is not valid on opcua server \"{server.Name}\""));
                    }
                }
            }
            else if (server.Endpoint is not null)
            {
                errors.Add(new ConfigError(path,
                    $"server.endpoint is only valid for the opcua protocol, set on \"{server.Name}\" ({server.Protocol})"));
            }
            CheckRange(server.Baud, "server.baud", 110, 4_000_000, path, errors);
            CheckChoice(server.Parity, ["none", "even", "odd"], "server.parity", path, errors,
                defaultIs: "even", context: $"[server \"{server.Name}\"]");
            CheckRange(server.DataBits, "server.data_bits", 7, 8, path, errors);
            CheckRange(server.StopBits, "server.stop_bits", 1, 2, path, errors);
            CheckRange(server.IdleGapMs, "server.idle_gap_ms", 1, 1000, path, errors);
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

    // --- client (master) validation -----------------------------------------

    /// <summary>Per-client: name/device/remote_addr required, ranges, poll and push entries.</summary>
    private static void ValidateClients(MozConfig config, string path, List<ConfigError> errors)
    {
        var devices = config.Device.ToDictionary(d => d.Name, StringComparer.Ordinal);
        foreach (var client in config.Client)
        {
            var ctx = $"[[client]] \"{client.Name}\"";
            Require(client.Name, "client.name", path, errors, ctx);
            Require(client.Device, "client.device", path, errors, ctx);
            Require(client.RemoteAddr, "client.remote_addr", path, errors, ctx);
            CheckExtra(client.Extra, ctx, path, errors);
            CheckRange(client.RemoteUnit, "client.remote_unit", 0, Modbus.ModbusLimits.UnitIdMax, path, errors);
            CheckRange(client.IntervalMs, "client.interval_ms", 100, 3_600_000, path, errors);
            CheckRange(client.TimeoutMs, "client.timeout_ms", 10, 600_000, path, errors);

            if (client.RemoteAddr.Length > 0 && !TryParseRemoteAddr(client.RemoteAddr))
            {
                errors.Add(new ConfigError(path,
                    $"client.remote_addr value \"{client.RemoteAddr}\" is invalid (host:port, port 1..65535) in {ctx}"));
            }

            foreach (var poll in client.Poll)
            {
                var pctx = $"[[client.poll]] on \"{client.Name}\"";
                CheckExtra(poll.Extra, pctx, path, errors);
                bool isBit = poll.Fc is 1 or 2;
                if (poll.Fc is not (1 or 2 or 3 or 4))
                {
                    errors.Add(new ConfigError(path,
                        $"poll.fc value {poll.Fc} is invalid (1..4, reads only) in {pctx}"));
                }
                else
                {
                    int max = isBit ? Modbus.ModbusLimits.ReadBitsMax : Modbus.ModbusLimits.ReadRegsMax;
                    if (poll.Count < 1 || poll.Count > max)
                    {
                        errors.Add(new ConfigError(path,
                            $"poll.count value {poll.Count} is out of range 1..{max} in {pctx}"));
                    }
                }
                if (poll.RemoteOffset < 0 || poll.RemoteOffset > ushort.MaxValue)
                {
                    errors.Add(new ConfigError(path,
                        $"poll.remote_offset value {poll.RemoteOffset} is out of range 0..65535 in {pctx}"));
                }
                if (poll.LocalOffset < 0 || poll.LocalOffset > ushort.MaxValue)
                {
                    errors.Add(new ConfigError(path,
                        $"poll.local_offset value {poll.LocalOffset} is out of range 0..65535 in {pctx}"));
                }
                CheckLocalKind(poll.LocalKind, poll.Fc, isBit, "poll", pctx, path, errors);
            }

            devices.TryGetValue(client.Device, out var device);
            foreach (var push in client.Push)
            {
                var pctx = $"[[client.push]] on \"{client.Name}\"";
                CheckExtra(push.Extra, pctx, path, errors);
                bool isBit = push.Fc is 5 or 15;
                if (push.Fc is not (5 or 6 or 15 or 16))
                {
                    errors.Add(new ConfigError(path,
                        $"push.fc value {push.Fc} is invalid (5|6|15|16, writes only) in {pctx}"));
                }
                else
                {
                    int max = push.Fc switch
                    {
                        5 or 6 => 1,
                        15 => Modbus.ModbusLimits.WriteBitsMax,
                        _ => Modbus.ModbusLimits.WriteRegsMax,
                    };
                    if (push.Count < 1 || push.Count > max)
                    {
                        errors.Add(new ConfigError(path,
                            $"push.count value {push.Count} is out of range 1..{max} in {pctx}"));
                    }
                }
                if (push.RemoteOffset < 0 || push.RemoteOffset > ushort.MaxValue)
                {
                    errors.Add(new ConfigError(path,
                        $"push.remote_offset value {push.RemoteOffset} is out of range 0..65535 in {pctx}"));
                }
                if (push.LocalOffset < 0 || push.LocalOffset > ushort.MaxValue)
                {
                    errors.Add(new ConfigError(path,
                        $"push.local_offset value {push.LocalOffset} is out of range 0..65535 in {pctx}"));
                }
                var table = CheckLocalKind(push.LocalKind, push.Fc, isBit, "push", pctx, path, errors);
                // the local range is read on every push: it must exist on the device
                if (device is not null && table is { } t && push.Count >= 1 && push.LocalOffset >= 0)
                {
                    int length = t switch
                    {
                        TableKind.Coil => device.Coils,
                        TableKind.DiscreteInput => device.Discrete,
                        TableKind.HoldingRegister => device.Holding,
                        _ => device.Input,
                    };
                    if (push.LocalOffset + push.Count > length)
                    {
                        errors.Add(new ConfigError(path,
                            $"push local_offset {push.LocalOffset} (+{push.Count} cells) exceeds device \"{device.Name}\" " +
                            $"{push.LocalKind} length {length} in {pctx}"));
                    }
                }
            }
        }

        // device references resolve
        foreach (var client in config.Client)
        {
            if (client.Device.Length > 0 && !devices.ContainsKey(client.Device))
            {
                errors.Add(new ConfigError(path,
                    $"[client \"{client.Name}\"] references device \"{client.Device}\" which is not declared"));
            }
        }
    }

    /// <summary>local_kind must spell a table whose bitness matches the fc. Returns the table when valid.</summary>
    private static TableKind? CheckLocalKind(string? kind, int fc, bool isBit, string key, string ctx,
        string path, List<ConfigError> errors)
    {
        if (kind is null)
        {
            return null; // invalid fc: no default, reported above
        }
        if (!LocalKinds.TryParse(kind, out var table))
        {
            errors.Add(new ConfigError(path,
                $"{key}.local_kind value \"{kind}\" is invalid (coil|discrete|holding|input) in {ctx}"));
            return null;
        }
        if (table.IsBit() != isBit)
        {
            errors.Add(new ConfigError(path, isBit
                ? $"bit fc {fc} requires a bit local kind (coil|discrete) in {ctx}"
                : $"word fc {fc} requires a word local kind (holding|input) in {ctx}"));
            return null;
        }
        return table;
    }

    private static bool TryParseOpcUaEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != "opc.tcp" ||
            uri.Port is < 1 or > 65535)
        {
            return false;
        }
        return !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static bool TryParseRemoteAddr(string addr)
    {
        // host:port (host may be an IP or hostname; IPv6 literals are v1-unsupported)
        var sep = addr.LastIndexOf(':');
        if (sep <= 0 || sep == addr.Length - 1)
        {
            return false;
        }
        return int.TryParse(addr[(sep + 1)..], out var port) && port >= 1 && port <= 65535;
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
                (defaultIs is null ? ")" : $"; default \"{defaultIs}\")") +
                (context is null ? "" : $" in {context}")));
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