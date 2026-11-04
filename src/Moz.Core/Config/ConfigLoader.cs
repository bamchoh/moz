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
            if (server.Protocol == "opcua")
            {
                server.Anonymous ??= server.User.Count > 0 ? "deny" : "write";
                server.Security ??= ["none"];
                server.AcceptUntrusted ??= true;
            }
        }
        foreach (var client in config.Client)
        {
            if (client.IsCellBased)
            {
                foreach (var poll in client.Poll)
                {
                    poll.Count ??= 1;
                    poll.LocalOffset ??= 0;
                }
                foreach (var push in client.Push)
                {
                    push.Count ??= 1;
                    push.LocalOffset ??= 0;
                }
            }
            if (client.IsModbus)
            {
                client.RemoteUnit ??= 1;
                foreach (var poll in client.Poll)
                {
                    poll.Fc ??= 3;
                    poll.RemoteOffset ??= 0;
                    poll.LocalKind ??= PollConfig.DefaultLocalKind(poll.Fc.Value);
                }
                foreach (var push in client.Push)
                {
                    push.Fc ??= 16;
                    push.RemoteOffset ??= 0;
                    push.LocalKind ??= PushConfig.DefaultLocalKind(push.Fc.Value);
                }
            }
            else if (client.Protocol == "opcua")
            {
                client.Security ??= "none";
                client.AcceptUntrusted ??= true;
            }
            else if (client.Protocol == "mc-3e")
            {
                client.Network ??= 0;
                client.Pc ??= 0xFF;
                client.ModuleIo ??= 0x03FF;
                client.ModuleStation ??= 0;
                foreach (var poll in client.Poll)
                {
                    poll.LocalKind ??= McDefaultLocalKind(poll.Remote);
                }
                foreach (var push in client.Push)
                {
                    push.LocalKind ??= McDefaultLocalKind(push.Remote);
                }
            }
        }
    }

    /// <summary>Bit devices mirror into coils, word devices into holding registers; null for an unparseable device (reported by Validate).</summary>
    private static string? McDefaultLocalKind(string? remote) =>
        remote is not null && Mc.McDevice.TryParse(remote, out var device, out _) ? (device.IsBit ? "coil" : "holding") : null;

    public static string DefaultConfigHint() =>
        "expected a [log] table, [[device]] tables, and [[server]] tables (see docs/spec-config.md)";

    // --- validation --------------------------------------------------------

    private static bool Ok(MozConfig? config, List<ConfigError> errors) =>
        config is not null && errors.Count == 0;

    private static readonly string[] LogLevels = ["trace", "debug", "info", "warn", "error"];
    private static readonly string[] LogFormats = ["text", "json"];
    private static readonly string[] Protocols = ["modbus-tcp", "modbus-rtu", "opcua", "mc-3e", "fins-tcp", "fins-udp"];
    private static readonly string[] ClientProtocols = ["modbus-tcp", "modbus-rtu", "mc-3e", "opcua"];

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

            // opcua user management (phase 15) and secure channels (phase 16)
            if (server.Protocol == "opcua")
            {
                ValidateOpcUaUsers(server, path, errors);
                ValidateOpcUaSecurity(server, path, errors);
            }
            else
            {
                if (server.User.Count > 0)
                {
                    errors.Add(new ConfigError(path, $"server.user is only valid for opcua servers, set on \"{server.Name}\" ({server.Protocol})"));
                }
                foreach (var (set, key) in new[] { (server.Anonymous is not null, "anonymous"), (server.Security is not null, "security"),
                                                   (server.AcceptUntrusted is not null, "accept_untrusted"), (server.Pki is not null, "pki") })
                {
                    if (set)
                    {
                        errors.Add(new ConfigError(path, $"server.{key} is only valid for opcua servers, set on \"{server.Name}\" ({server.Protocol})"));
                    }
                }
            }
            CheckSerial("server", server.Baud, server.Parity, server.DataBits, server.StopBits, server.IdleGapMs,
                $"[server \"{server.Name}\"]", path, errors);

            // mc-3e (phase 13) and fins (phase 19) wiring: device / area maps, FINS node
            var mappedDevice = config.Device.FirstOrDefault(d => d.Name == server.Device);
            if (server.Protocol == "mc-3e")
            {
                ValidateMaps(server, mappedDevice, path, errors, ResolveMcDevice, "device code letters only, e.g. D, M, W, ZR");
            }
            else if (server.IsFins)
            {
                ValidateMaps(server, mappedDevice, path, errors, ResolveFinsArea, "DM|CIO|W|H|A|E0..E18");
            }
            else if (server.Map.Count > 0)
            {
                errors.Add(new ConfigError(path,
                    $"server.map is only valid for mc-3e and fins servers, set on \"{server.Name}\" ({server.Protocol})"));
            }
            if (server.Node is { } node)
            {
                if (!server.IsFins)
                {
                    errors.Add(new ConfigError(path, $"server.node is only valid for fins servers, set on \"{server.Name}\" ({server.Protocol})"));
                }
                else
                {
                    CheckRange(node, "server.node", 1, 254, path, errors);
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
            CheckExtra(client.Extra, ctx, path, errors);
            CheckChoice(client.Protocol, ClientProtocols, "client.protocol", path, errors,
                defaultIs: "modbus-tcp", context: ctx);
            CheckRange(client.IntervalMs, "client.interval_ms", 100, 3_600_000, path, errors);
            CheckRange(client.TimeoutMs, "client.timeout_ms", 10, 600_000, path, errors);

            // protocol-specific client keys
            if (client.IsModbus)
            {
                CheckRange(client.RemoteUnit!.Value, "client.remote_unit", 0, Modbus.ModbusLimits.UnitIdMax, path, errors);
            }
            else if (client.RemoteUnit is not null)
            {
                errors.Add(new ConfigError(path, $"client.remote_unit is only valid for modbus clients, set in {ctx} ({client.Protocol})"));
            }
            if (client.Protocol == "mc-3e")
            {
                CheckRange(client.Network!.Value, "client.network", 0, 255, path, errors);
                CheckRange(client.Pc!.Value, "client.pc", 0, 255, path, errors);
                CheckRange(client.ModuleIo!.Value, "client.module_io", 0, 65535, path, errors);
                CheckRange(client.ModuleStation!.Value, "client.module_station", 0, 255, path, errors);
            }
            else
            {
                foreach (var (value, key) in new[] { (client.Network, "network"), (client.Pc, "pc"), (client.ModuleIo, "module_io"), (client.ModuleStation, "module_station") })
                {
                    if (value is not null)
                    {
                        errors.Add(new ConfigError(path, $"client.{key} is only valid for mc-3e clients, set in {ctx} ({client.Protocol})"));
                    }
                }
            }

            // transport keys: exactly the set the protocol needs
            if (client.Protocol == "modbus-rtu")
            {
                Require(client.SerialPort ?? "", "client.serial_port", path, errors, $"{ctx} (required for modbus-rtu)");
                if (client.RemoteAddr.Length > 0)
                {
                    errors.Add(new ConfigError(path,
                        $"client.remote_addr is only valid for modbus-tcp, set in {ctx} ({client.Protocol})"));
                }
            }
            else if (client.Protocol == "opcua")
            {
                Require(client.Endpoint ?? "", "client.endpoint", path, errors, $"{ctx} (required for opcua)");
                if (client.Endpoint is { Length: > 0 } ep && !TryParseOpcUaEndpoint(ep))
                {
                    errors.Add(new ConfigError(path,
                        $"client.endpoint value \"{ep}\" is invalid (opc.tcp://host:port, port 1..65535) in {ctx}"));
                }
                if (client.RemoteAddr.Length > 0)
                {
                    errors.Add(new ConfigError(path,
                        $"client.remote_addr is only valid for modbus-tcp and mc-3e clients, set in {ctx} ({client.Protocol})"));
                }
                if (client.SerialPort is not null)
                {
                    errors.Add(new ConfigError(path,
                        $"client.serial_port is only valid for serial protocols (modbus-rtu), set in {ctx} ({client.Protocol})"));
                }
                if (client.Security is { } sec && OpcUa.OpcUaSecurity.Parse(sec) is null)
                {
                    errors.Add(new ConfigError(path, $"client.security value \"{sec}\" is invalid ({OpcUa.OpcUaSecurity.Spellings}) in {ctx}"));
                }
                if (client.User is not null && string.IsNullOrEmpty(client.Password))
                {
                    errors.Add(new ConfigError(path, $"client.password is required when client.user is set in {ctx}"));
                }
                if (client.Password is not null && string.IsNullOrEmpty(client.User))
                {
                    errors.Add(new ConfigError(path, $"client.user is required when client.password is set in {ctx}"));
                }
            }
            else
            {
                Require(client.RemoteAddr, "client.remote_addr", path, errors, ctx);
                if (client.RemoteAddr.Length > 0 && !TryParseRemoteAddr(client.RemoteAddr))
                {
                    errors.Add(new ConfigError(path,
                        $"client.remote_addr value \"{client.RemoteAddr}\" is invalid (host:port, port 1..65535) in {ctx}"));
                }
                if (client.SerialPort is not null)
                {
                    errors.Add(new ConfigError(path,
                        $"client.serial_port is only valid for serial protocols (modbus-rtu), set in {ctx} ({client.Protocol})"));
                }
            }
            if (client.Protocol != "opcua")
            {
                foreach (var (set, key) in new[] { (client.Endpoint is not null, "endpoint"), (client.Security is not null, "security"),
                                                   (client.User is not null, "user"), (client.Password is not null, "password"),
                                                   (client.AcceptUntrusted is not null, "accept_untrusted"), (client.Pki is not null, "pki") })
                {
                    if (set)
                    {
                        errors.Add(new ConfigError(path, $"client.{key} is only valid for opcua clients, set in {ctx} ({client.Protocol})"));
                    }
                }
            }
            CheckSerial("client", client.Baud, client.Parity, client.DataBits, client.StopBits, client.IdleGapMs, ctx, path, errors);

            devices.TryGetValue(client.Device, out var device);
            var leaves = client.Protocol == "opcua" ? LeafNames(config, client.Device) : null;
            foreach (var poll in client.Poll)
            {
                var pctx = $"[[client.poll]] on \"{client.Name}\"";
                CheckExtra(poll.Extra, pctx, path, errors);
                if (client.Protocol == "opcua")
                {
                    ValidateOpcUaItem("poll", poll.Node, poll.Variable, poll.Count, poll.LocalOffset, poll.LocalKind,
                        poll.Fc, poll.RemoteOffset, poll.Remote, leaves!, client.Device, pctx, path, errors);
                    continue;
                }
                CheckNotOpcUaKeys("poll", poll.Node, poll.Variable, client.Protocol, pctx, path, errors);
                int localOffset = poll.LocalOffset!.Value;
                int count = poll.Count!.Value;
                if (localOffset < 0 || localOffset > ushort.MaxValue)
                {
                    errors.Add(new ConfigError(path,
                        $"poll.local_offset value {localOffset} is out of range 0..65535 in {pctx}"));
                }
                TableKind? table;
                if (client.IsModbus)
                {
                    table = ValidateModbusPoll(poll, count, pctx, path, errors);
                }
                else
                {
                    CheckNotModbusKeys("poll", poll.Fc, poll.RemoteOffset, client.Protocol, pctx, path, errors);
                    table = ValidateMcItem("poll", poll.Remote, count, poll.LocalKind, pctx, path, errors);
                }
                CheckLocalRange("poll", device, table, poll.LocalKind, localOffset, count, pctx, path, errors);
            }

            foreach (var push in client.Push)
            {
                var pctx = $"[[client.push]] on \"{client.Name}\"";
                CheckExtra(push.Extra, pctx, path, errors);
                if (client.Protocol == "opcua")
                {
                    ValidateOpcUaItem("push", push.Node, push.Variable, push.Count, push.LocalOffset, push.LocalKind,
                        push.Fc, push.RemoteOffset, push.Remote, leaves!, client.Device, pctx, path, errors);
                    continue;
                }
                CheckNotOpcUaKeys("push", push.Node, push.Variable, client.Protocol, pctx, path, errors);
                int localOffset = push.LocalOffset!.Value;
                int count = push.Count!.Value;
                if (localOffset < 0 || localOffset > ushort.MaxValue)
                {
                    errors.Add(new ConfigError(path,
                        $"push.local_offset value {localOffset} is out of range 0..65535 in {pctx}"));
                }
                TableKind? table;
                if (client.IsModbus)
                {
                    table = ValidateModbusPush(push, count, pctx, path, errors);
                }
                else
                {
                    CheckNotModbusKeys("push", push.Fc, push.RemoteOffset, client.Protocol, pctx, path, errors);
                    table = ValidateMcItem("push", push.Remote, count, push.LocalKind, pctx, path, errors);
                }
                CheckLocalRange("push", device, table, push.LocalKind, localOffset, count, pctx, path, errors);
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

    /// <summary>
    /// The local range of a poll (written every tick) or push (read on every
    /// change) must exist on the device; otherwise every tick would fail at
    /// runtime. Skipped when earlier checks already failed (no device, bad
    /// kind, bad count/offset).
    /// </summary>
    private static void CheckLocalRange(string key, DeviceConfig? device, TableKind? table, string? kind,
        int offset, int count, string ctx, string path, List<ConfigError> errors)
    {
        if (device is null || table is not { } t || count < 1 || offset < 0)
        {
            return;
        }
        int length = t switch
        {
            TableKind.Coil => device.Coils,
            TableKind.DiscreteInput => device.Discrete,
            TableKind.HoldingRegister => device.Holding,
            _ => device.Input,
        };
        if (offset + count > length)
        {
            errors.Add(new ConfigError(path,
                $"{key} local_offset {offset} (+{count} cells) exceeds device \"{device.Name}\" {kind} length {length} in {ctx}"));
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

    /// <summary>Modbus poll: fc 1..4, count within the read limit, remote_offset 0..65535, local_kind bitness = fc bitness.</summary>
    private static TableKind? ValidateModbusPoll(PollConfig poll, int count, string pctx, string path, List<ConfigError> errors)
    {
        if (poll.Remote is not null)
        {
            errors.Add(new ConfigError(path, $"poll.remote is only valid for mc-3e clients, set in {pctx}"));
        }
        int fc = poll.Fc!.Value;
        bool isBit = fc is 1 or 2;
        if (fc is not (1 or 2 or 3 or 4))
        {
            errors.Add(new ConfigError(path, $"poll.fc value {fc} is invalid (1..4, reads only) in {pctx}"));
        }
        else
        {
            int max = isBit ? Modbus.ModbusLimits.ReadBitsMax : Modbus.ModbusLimits.ReadRegsMax;
            if (count < 1 || count > max)
            {
                errors.Add(new ConfigError(path, $"poll.count value {count} is out of range 1..{max} in {pctx}"));
            }
        }
        if (poll.RemoteOffset is < 0 or > ushort.MaxValue)
        {
            errors.Add(new ConfigError(path, $"poll.remote_offset value {poll.RemoteOffset} is out of range 0..65535 in {pctx}"));
        }
        return CheckLocalKind(poll.LocalKind, fc, isBit, "poll", pctx, path, errors);
    }

    /// <summary>Modbus push: fc 5|6|15|16, count within the write limit (1 for fc 5/6), remote_offset 0..65535, local_kind bitness = fc bitness.</summary>
    private static TableKind? ValidateModbusPush(PushConfig push, int count, string pctx, string path, List<ConfigError> errors)
    {
        if (push.Remote is not null)
        {
            errors.Add(new ConfigError(path, $"push.remote is only valid for mc-3e clients, set in {pctx}"));
        }
        int fc = push.Fc!.Value;
        bool isBit = fc is 5 or 15;
        if (fc is not (5 or 6 or 15 or 16))
        {
            errors.Add(new ConfigError(path, $"push.fc value {fc} is invalid (5|6|15|16, writes only) in {pctx}"));
        }
        else
        {
            int max = fc switch
            {
                5 or 6 => 1,
                15 => Modbus.ModbusLimits.WriteBitsMax,
                _ => Modbus.ModbusLimits.WriteRegsMax,
            };
            if (count < 1 || count > max)
            {
                errors.Add(new ConfigError(path, $"push.count value {count} is out of range 1..{max} in {pctx}"));
            }
        }
        if (push.RemoteOffset is < 0 or > ushort.MaxValue)
        {
            errors.Add(new ConfigError(path, $"push.remote_offset value {push.RemoteOffset} is out of range 0..65535 in {pctx}"));
        }
        return CheckLocalKind(push.LocalKind, fc, isBit, "push", pctx, path, errors);
    }

    /// <summary>Leaf variable names declared on a device (struct declarations expanded); unexpandable declarations are reported by variable validation.</summary>
    private static HashSet<string> LeafNames(MozConfig config, string deviceName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var structs = ToStructDefs(config);
        foreach (var v in config.Variable.Where(v => v.Device == deviceName))
        {
            try
            {
                foreach (var leaf in VariableLayout.Expand(v.Name, v.ToTableKind(), v.Offset, v.Type, v.Count, v.Length ?? 0, structs))
                {
                    names.Add(leaf.Name);
                }
            }
            catch (Exception)
            {
                // invalid declaration: its own error is reported elsewhere
            }
        }
        return names;
    }

    /// <summary>opcua poll/push: node id text, a declared leaf variable, and none of the cell-based keys.</summary>
    private static void ValidateOpcUaItem(string key, string? node, string? variable, int? count, int? localOffset, string? localKind,
        int? fc, int? remoteOffset, string? remote, HashSet<string> leaves, string deviceName, string ctx, string path, List<ConfigError> errors)
    {
        Require(node ?? "", $"{key}.node", path, errors, $"{ctx} (e.g. ns=2;s=Temp, nsu=urn:x;s=Temp, i=2258)");
        if (!string.IsNullOrWhiteSpace(node) && !OpcUa.OpcUaRemote.TryParseNode(node))
        {
            errors.Add(new ConfigError(path, $"{key}.node value \"{node}\" is invalid (e.g. ns=2;s=Temp, nsu=urn:x;s=Temp, i=2258) in {ctx}"));
        }
        Require(variable ?? "", $"{key}.variable", path, errors, $"{ctx} (a [[variable]] on device \"{deviceName}\")");
        if (!string.IsNullOrWhiteSpace(variable) && !leaves.Contains(variable))
        {
            errors.Add(new ConfigError(path,
                $"{key}.variable value \"{variable}\" is not declared on device \"{deviceName}\" (struct variables are addressed by leaf name, e.g. motor.speed) in {ctx}"));
        }
        foreach (var (set, name) in new[] { (count is not null, "count"), (localOffset is not null, "local_offset"), (localKind is not null, "local_kind") })
        {
            if (set)
            {
                errors.Add(new ConfigError(path, $"{key}.{name} is only valid for modbus and mc-3e clients (the variable defines the local cells), set in {ctx}"));
            }
        }
        CheckNotModbusKeys(key, fc, remoteOffset, "opcua", ctx, path, errors);
        if (remote is not null)
        {
            errors.Add(new ConfigError(path, $"{key}.remote is only valid for mc-3e clients, set in {ctx} (opcua)"));
        }
    }

    /// <summary>opcua keys on a cell-based client are errors, not silently ignored.</summary>
    private static void CheckNotOpcUaKeys(string key, string? node, string? variable, string protocol, string ctx, string path, List<ConfigError> errors)
    {
        if (node is not null)
        {
            errors.Add(new ConfigError(path, $"{key}.node is only valid for opcua clients, set in {ctx} ({protocol})"));
        }
        if (variable is not null)
        {
            errors.Add(new ConfigError(path, $"{key}.variable is only valid for opcua clients, set in {ctx} ({protocol})"));
        }
    }

    /// <summary>Modbus keys on a non-Modbus client are errors, not silently ignored.</summary>
    private static void CheckNotModbusKeys(string key, int? fc, int? remoteOffset, string protocol, string ctx, string path, List<ConfigError> errors)
    {
        if (fc is not null)
        {
            errors.Add(new ConfigError(path, $"{key}.fc is only valid for modbus clients, set in {ctx} ({protocol})"));
        }
        if (remoteOffset is not null)
        {
            errors.Add(new ConfigError(path, $"{key}.remote_offset is only valid for modbus clients, set in {ctx} ({protocol})"));
        }
    }

    /// <summary>
    /// mc-3e poll/push: remote in MELSEC notation, count within the batch
    /// limit of the device's bitness, head + count within 24 bits, local_kind
    /// bitness = device bitness. Returns the local table when valid.
    /// </summary>
    private static TableKind? ValidateMcItem(string key, string? remote, int count, string? localKind,
        string ctx, string path, List<ConfigError> errors)
    {
        Require(remote ?? "", $"{key}.remote", path, errors, $"{ctx} (device notation, e.g. D100, M0, X1A)");
        if (string.IsNullOrWhiteSpace(remote))
        {
            return null;
        }
        if (!Mc.McDevice.TryParse(remote, out var device, out var address))
        {
            errors.Add(new ConfigError(path,
                $"{key}.remote value \"{remote}\" is invalid (device code + number, e.g. D100, M0, X1A; number within 24 bits) in {ctx}"));
            return null;
        }
        int max = device.IsBit ? Mc.Mc3E.ReadBitsMax : Mc.Mc3E.ReadWordsMax;
        if (count < 1 || count > max)
        {
            errors.Add(new ConfigError(path, $"{key}.count value {count} is out of range 1..{max} in {ctx}"));
        }
        else if (address + count > Mc.McDevice.AddressMax + 1)
        {
            errors.Add(new ConfigError(path,
                $"{key}.remote value \"{remote}\" + count {count} exceeds the 24-bit device address range in {ctx}"));
        }
        if (localKind is null)
        {
            return null;
        }
        if (!LocalKinds.TryParse(localKind, out var table))
        {
            errors.Add(new ConfigError(path, $"{key}.local_kind value \"{localKind}\" is invalid (coil|discrete|holding|input) in {ctx}"));
            return null;
        }
        if (table.IsBit() != device.IsBit)
        {
            errors.Add(new ConfigError(path, device.IsBit
                ? $"{key}.local_kind value \"{localKind}\" does not match the bit device {device.Code} (coil|discrete) in {ctx}"
                : $"{key}.local_kind value \"{localKind}\" does not match the word device {device.Code} (holding|input) in {ctx}"));
            return null;
        }
        return table;
    }

    /// <summary>[[server.user]]: name and password present, access read|write, unique names; server.anonymous write|read|deny.</summary>
    private static void ValidateOpcUaUsers(ServerConfig server, string path, List<ConfigError> errors)
    {
        var ctx = $"[server \"{server.Name}\"]";
        CheckChoice(server.Anonymous, ["write", "read", "deny"], "server.anonymous", path, errors, defaultIs: null, context: ctx);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in server.User)
        {
            CheckExtra(user.Extra, $"[[server.user]] on \"{server.Name}\"", path, errors);
            Require(user.Name, "server.user.name", path, errors, ctx);
            Require(user.Password, "server.user.password", path, errors, $"{ctx} user \"{user.Name}\"");
            CheckChoice(user.Access, ["read", "write"], "server.user.access", path, errors, defaultIs: "write", context: ctx);
            if (user.Name.Length > 0 && !seen.Add(user.Name))
            {
                errors.Add(new ConfigError(path, $"duplicate server.user name \"{user.Name}\" in {ctx}"));
            }
        }
    }

    /// <summary>server.security: one or more of none | sign | sign-encrypt, no duplicates.</summary>
    private static void ValidateOpcUaSecurity(ServerConfig server, string path, List<ConfigError> errors)
    {
        var ctx = $"[server \"{server.Name}\"]";
        if (server.Security is { Count: 0 })
        {
            errors.Add(new ConfigError(path, $"server.security must list at least one of none|sign|sign-encrypt in {ctx}"));
            return;
        }
        var seen = new HashSet<OpcUa.OpcUaSecurity>();
        foreach (var value in server.Security ?? [])
        {
            var parsed = OpcUa.OpcUaSecurity.Parse(value);
            if (parsed is null)
            {
                errors.Add(new ConfigError(path, $"server.security value \"{value}\" is invalid ({OpcUa.OpcUaSecurity.Spellings}) in {ctx}"));
            }
            else if (!seen.Add(parsed.Value))
            {
                errors.Add(new ConfigError(path, $"duplicate server.security value \"{parsed.Value}\" in {ctx}"));
            }
        }
    }

    /// <summary>[[server.map]]: known device code, kind of matching bitness, range within the device table, no duplicate codes.</summary>
    /// <summary>The canonical code and bitness of an MC device / FINS area; null when unknown.</summary>
    private static (string Code, bool IsBit)? ResolveMcDevice(string text) =>
        Mc.McDevice.TryFromCode(text, out var dev) ? (dev.Code, dev.IsBit) : null;

    private static (string Code, bool IsBit)? ResolveFinsArea(string text) =>
        Fins.FinsArea.TryFromCode(text, out var area) ? (area.Code, false) : null;

    /// <summary>[[server.map]] lines (mc-3e and fins): known code, unique, kind bitness matches, range within the device table.</summary>
    private static void ValidateMaps(ServerConfig server, DeviceConfig? device, string path, List<ConfigError> errors,
        Func<string, (string Code, bool IsBit)?> resolve, string hint)
    {
        var ctx = $"[server \"{server.Name}\"]";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in server.Map)
        {
            CheckExtra(map.Extra, $"[[server.map]] on \"{server.Name}\"", path, errors);
            if (resolve(map.Device) is not { } dev)
            {
                errors.Add(new ConfigError(path,
                    $"server.map device value \"{map.Device}\" is invalid ({hint}) in {ctx}"));
                continue;
            }
            if (!seen.Add(dev.Code))
            {
                errors.Add(new ConfigError(path, $"duplicate server.map device \"{dev.Code}\" in {ctx}"));
            }
            if (map.Offset < 0 || map.Offset > ushort.MaxValue)
            {
                errors.Add(new ConfigError(path, $"server.map offset value {map.Offset} is out of range 0..65535 in {ctx}"));
            }
            if (map.Count is { } c && (c < 1 || c > 65536))
            {
                errors.Add(new ConfigError(path, $"server.map count value {c} is out of range 1..65536 in {ctx}"));
            }
            if (!LocalKinds.TryParse(map.Kind, out var table))
            {
                errors.Add(new ConfigError(path, $"server.map kind value \"{map.Kind}\" is invalid (coil|discrete|holding|input) in {ctx}"));
                continue;
            }
            if (table.IsBit() != dev.IsBit)
            {
                errors.Add(new ConfigError(path, dev.IsBit
                    ? $"server.map kind value \"{map.Kind}\" does not match the bit device {dev.Code} (coil|discrete) in {ctx}"
                    : $"server.map kind value \"{map.Kind}\" does not match the word device {dev.Code} (holding|input) in {ctx}"));
                continue;
            }
            if (device is null || map.Offset < 0 || map.Count is < 1)
            {
                continue;
            }
            int length = table switch
            {
                TableKind.Coil => device.Coils,
                TableKind.DiscreteInput => device.Discrete,
                TableKind.HoldingRegister => device.Holding,
                _ => device.Input,
            };
            int cells = map.Count ?? Math.Max(0, length - map.Offset);
            if (map.Offset + cells > length || cells == 0)
            {
                errors.Add(new ConfigError(path,
                    $"server.map {dev.Code} offset {map.Offset} (+{cells} cells) exceeds device \"{device.Name}\" {map.Kind} length {length} in {ctx}"));
            }
        }
    }

    /// <summary>Serial line settings, shared by [[server]] (slave) and [[client]] (master).</summary>
    private static void CheckSerial(string key, int baud, string parity, int dataBits, int stopBits, int idleGapMs,
        string ctx, string path, List<ConfigError> errors)
    {
        CheckRange(baud, $"{key}.baud", 110, 4_000_000, path, errors);
        CheckChoice(parity, ["none", "even", "odd"], $"{key}.parity", path, errors, defaultIs: "even", context: ctx);
        CheckRange(dataBits, $"{key}.data_bits", 7, 8, path, errors);
        CheckRange(stopBits, $"{key}.stop_bits", 1, 2, path, errors);
        CheckRange(idleGapMs, $"{key}.idle_gap_ms", 1, 1000, path, errors);
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