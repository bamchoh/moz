using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Moz;
using Moz.Client;
using Moz.Config;
using Moz.Devices;
using Moz.Logging;
using Moz.Fins;
using Moz.Mc;
using Moz.Modbus.Client;
using Moz.OpcUa;
using Moz.Modbus.Rtu;
using Moz.Modbus.Tcp;
using Moz.Scripts;

namespace Moz.Cli;

/// <summary>
/// CLI entry logic. The console and the factory switch live here — the ONE
/// place where config entries become implementations. Libraries never touch
/// the console; a future GUI reuses Moz.Core and builds its own wiring.
/// </summary>
public static class CliApp
{
    public static async Task<int> Run(string[] args)
    {
        // moz validate config.toml — same loader as serve, one validation path
        if (args.Length == 2 && args[0] == "validate")
        {
            return Validate(args[1]);
        }
        // moz config.toml — start all servers
        if (args.Length == 1)
        {
            if (args[0] is "-h" or "--help")
            {
                PrintUsage();
                return 0;
            }
            return await ServeAsync(args[0]);
        }
        PrintUsage(error: true);
        return 2;
    }

    private static int Validate(string configPath)
    {
        var result = ConfigLoader.Load(configPath);
        if (result.Ok)
        {
            return 0;
        }
        PrintConfigErrors(result);
        return 2;
    }

    /// <summary>One error format for both commands: path + message, one per line.</summary>
    private static void PrintConfigErrors(ConfigResult result)
    {
        Console.Error.WriteLine($"config has {result.Errors.Count} problem(s):");
        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"  {error}");
        }
    }

    private static void PrintUsage(bool error = false)
    {
        var writer = error ? Console.Error : Console.Out;
        writer.WriteLine(
            """
            usage: moz <command>

            commands:
              moz <config.toml>         start all servers from the config
              moz validate <config>     check the config and exit (0 ok / 2 problems)
              moz --help                show this help

            config format: docs/spec-config.md
            """);
    }

    private static async Task<int> ServeAsync(string configPath)
    {
        var result = ConfigLoader.Load(configPath);
        if (!result.Ok)
        {
            PrintConfigErrors(result);
            return 2;
        }
        var config = result.Config!;
        var log = config.Log ?? new LogConfig(); // FillDefaults guarantees non-null

        // --- logging: the CLI owns sinks; libraries only receive ILogger -----

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(ToLevel(log.Level));
            builder.AddConsole(o =>
            {
                o.FormatterName = log.Format == "json"
                    ? ConsoleFormatterNames.Json
                    : ConsoleFormatterNames.Simple;
            });
            if (!string.IsNullOrWhiteSpace(log.File))
            {
                builder.AddProvider(new FileLoggerProvider(log.File));
            }
        });
        var logger = loggerFactory.CreateLogger("moz");

        // --- wire config to implementations: the ONE factory switch ----------

        var devices = new Dictionary<string, Device>();
        foreach (var d in config.Device)
        {
            devices[d.Name] = new Device(
                d.Name,
                bits: d.Bits,
                readOnlyBits: d.ReadOnlyBits,
                words: d.Words,
                readOnlyWords: d.ReadOnlyWords);
        }

        // typed variable overlays (phase 3): per-device change logging +
        // the named access surface scripts and the OPC UA server use
        var variableTables = new Dictionary<string, VariableTable>();
        var structs = ConfigLoader.ToStructDefs(config);
        var leafIndex = new Dictionary<string, Dictionary<string, Variable>>();
        foreach (var d in config.Device)
        {
            // struct-typed declarations expand into dotted leaf variables here;
            // the VariableTable (and everything above it) only sees leaves
            var declared = config.Variable.Where(v => v.Device == d.Name).Select(v =>
                (Config: v, Leaves: VariableLayout.Expand(v.Name, v.ToTableKind(), v.Offset, v.Type, v.Count, v.Length ?? 0, structs))).ToList();
            variableTables[d.Name] = new VariableTable(
                devices[d.Name], declared.SelectMany(x => x.Leaves), loggerFactory!.CreateLogger($"moz.device.{d.Name}"));
            leafIndex[d.Name] = declared.SelectMany(x => x.Leaves).ToDictionary(v => v.Name, StringComparer.Ordinal);
            // initial values (value = ...): seeded before any runner starts, no change log
            foreach (var (v, leaves) in declared)
            {
                if (v.Value is { } value)
                {
                    InitialValues.Apply(devices[d.Name], InitialValues.Resolve(v.Name, v.Count, value, leaves));
                }
            }
        }

        var runners = new List<IProtocolRunner>(); // clients and scripts: started once the servers are ready
        var servers = new List<IProtocolRunner>();

        // paths in the config (scripts, pki folders) resolve relative to the config file
        var configDir = Path.GetDirectoryName(Path.GetFullPath(result.File))!;

        // clients (phase 6/11/12): the generic poll/push loop (ClientPoller) over a
        // protocol-specific IRemoteDevice. Each poll/push range is also recorded as
        // a ClientLink so scripts can ask plc.getVariableStatus
        var links = new Dictionary<string, List<ClientLink>>();
        foreach (var c in config.Client)
        {
            var clientLogger = loggerFactory.CreateLogger($"moz.client.{c.Name}");
            var poller = new ClientPoller(c.Name, BuildRemote(c, leafIndex[c.Device], configDir, clientLogger), c.IntervalMs, devices[c.Device], clientLogger);
            runners.Add(poller);
            var deviceLinks = links.TryGetValue(c.Device, out var l) ? l : links[c.Device] = [];
            deviceLinks.AddRange(poller.Remote.Polls.Concat(poller.Remote.Pushes)
                .Select(r => new ClientLink(r.Table, r.Offset, r.Count, () => poller.IsConnected)));
        }

        // periodic scripts (phase 4): paths resolve relative to the config file
        foreach (var s in config.Script)
        {
            var device = devices[s.Device];
            runners.Add(new ScriptRunner(
                s.Name,
                Path.Combine(configDir, s.Path),
                s.IntervalMs,
                device,
                variableTables[s.Device],
                loggerFactory.CreateLogger($"moz.script.{s.Name}"),
                links.TryGetValue(s.Device, out var scriptLinks) ? scriptLinks : null));
        }

        foreach (var s in config.Server)
        {
            var device = devices[s.Device];
            servers.Add(s.Protocol switch
            {
                "modbus-tcp" => new ModbusTcpServer(
                    s.Name,
                    new IPEndPoint(IPAddress.Parse(s.Bind!), s.Port),
                    device,
                    s.UnitIds is { Count: > 0 } ? s.UnitIds.Select(u => (ushort)u).ToArray() : null,
                    loggerFactory.CreateLogger($"moz.server.{s.Name}")),
                "opcua" => new OpcUaServerRunner(
                    s.Name,
                    s.Endpoint!,
                    device,
                    variableTables.TryGetValue(s.Device, out var vt)
                        ? vt
                        : new VariableTable(device, [], loggerFactory.CreateLogger($"moz.device.{s.Device}")),
                    loggerFactory.CreateLogger($"moz.server.{s.Name}"),
                    new OpcUaServerOptions(
                        s.User.Select(u => new OpcUaUser(u.Name, u.Password, u.Access == "write")).ToArray(),
                        s.Anonymous switch { "read" => OpcUaAccess.Read, "deny" => OpcUaAccess.Deny, _ => OpcUaAccess.Write },
                        (s.Security ?? ["none"]).Select(v => OpcUaSecurity.Parse(v) ?? OpcUaSecurity.None).ToArray(),
                        s.Pki is null ? null : Path.Combine(configDir, s.Pki),
                        s.AcceptUntrusted ?? true)),
                "mc-3e" => new McServer(
                    s.Name,
                    new IPEndPoint(IPAddress.Parse(s.Bind!), s.Port),
                    device,
                    McMap.Merge(device, s.Map.Select(m => McMapOf(m, device))),
                    loggerFactory.CreateLogger($"moz.server.{s.Name}")),
                "fins-tcp" => new FinsTcpServer(
                    s.Name,
                    new IPEndPoint(IPAddress.Parse(s.Bind!), s.Port),
                    (byte)(s.Node ?? FinsTcp.DefaultNode(IPAddress.Parse(s.Bind!))),
                    device,
                    FinsMap.Merge(device, s.Map.Select(m => FinsMapOf(m, device))),
                    loggerFactory.CreateLogger($"moz.server.{s.Name}")),
                "fins-udp" => new FinsUdpServer(
                    s.Name,
                    new IPEndPoint(IPAddress.Parse(s.Bind!), s.Port),
                    device,
                    FinsMap.Merge(device, s.Map.Select(m => FinsMapOf(m, device))),
                    loggerFactory.CreateLogger($"moz.server.{s.Name}")),
                "modbus-rtu" => new RtuServer(
                    s.Name,
                    OpenSerialPort(s.SerialPort!, s.Baud, s.Parity, s.DataBits, s.StopBits),
                    device,
                    s.UnitIds is { Count: > 0 } ? s.UnitIds.Select(u => (ushort)u).ToArray() : null,
                    s.IdleGapMs,
                    loggerFactory.CreateLogger($"moz.server.{s.Name}")),
                _ => throw new InvalidOperationException(
                    $"unknown protocol \"{s.Protocol}\" (already rejected by config validation)"),
            });
        }

        // --- run everything; Ctrl+C / process kill stops cleanly -------------

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            // servers first; clients/scripts once every server is ready (RunnerHost)
            await RunnerHost.RunAsync(servers, runners, TimeSpan.FromSeconds(30), logger, cts.Token);
        }
        catch (Exception ex)
        {
            // one runner failed fatally — report, exit
            logger.LogError(ex, "runner failed: {Message}", ex.Message);
            return 1;
        }

        logger.LogInformation("all runners stopped");
        return 0;
    }

    /// <summary>The ONE place a [[client]] protocol name becomes an IRemoteDevice.</summary>
    private static IRemoteDevice BuildRemote(ClientConfig c, IReadOnlyDictionary<string, Variable> leaves, string configDir, ILogger logger)
    {
        switch (c.Protocol)
        {
            case "modbus-tcp":
            case "modbus-rtu":
                IMasterLink link = c.Protocol == "modbus-tcp"
                    ? new TcpMasterLink(ParseRemoteAddr(c.RemoteAddr), c.TimeoutMs)
                    // the port is opened lazily (first cycle) and re-opened after a failure, like a TCP connect
                    : new RtuMasterLink(
                        () => OpenSerialPort(c.SerialPort!, c.Baud, c.Parity, c.DataBits, c.StopBits),
                        c.SerialPort!, c.TimeoutMs, c.IdleGapMs);
                return new ModbusRemote(c.Name, link, (byte)c.RemoteUnit!.Value,
                    c.Poll.Select(p => new PollEntry(p.Fc!.Value, p.RemoteOffset!.Value, p.Count!.Value, p.EffectiveLocalTable(), p.LocalOffset!.Value)).ToArray(),
                    c.Push.Select(p => new PushEntry(p.Fc!.Value, p.EffectiveLocalTable(), p.LocalOffset!.Value, p.Count!.Value, p.RemoteOffset!.Value)).ToArray(),
                    logger);
            case "mc-3e":
                return new McRemote(c.Name, ParseRemoteAddr(c.RemoteAddr), c.TimeoutMs,
                    new Mc3EOptions((byte)c.Network!.Value, (byte)c.Pc!.Value, (ushort)c.ModuleIo!.Value, (byte)c.ModuleStation!.Value,
                        Mc3E.TimerUnitsFor(c.TimeoutMs)),
                    c.Poll.Select(p => McEntryOf(p.Remote!, p.Count!.Value, p.EffectiveLocalTable(), p.LocalOffset!.Value)).ToArray(),
                    c.Push.Select(p => McEntryOf(p.Remote!, p.Count!.Value, p.EffectiveLocalTable(), p.LocalOffset!.Value)).ToArray(),
                    logger);
            case "opcua":
                return new OpcUaRemote(c.Name, c.Endpoint!, c.TimeoutMs,
                    c.Poll.Select(p => new OpcUaEntry(OpcUaRemote.ParseNode(p.Node!), leaves[p.Variable!])).ToArray(),
                    c.Push.Select(p => new OpcUaEntry(OpcUaRemote.ParseNode(p.Node!), leaves[p.Variable!])).ToArray(),
                    logger,
                    new OpcUaClientOptions(
                        OpcUaSecurity.Parse(c.Security ?? "none") ?? OpcUaSecurity.None,
                        c.User, c.Password,
                        c.Pki is null ? null : Path.Combine(configDir, c.Pki),
                        c.AcceptUntrusted ?? true));
            default:
                throw new InvalidOperationException(
                    $"unknown client protocol \"{c.Protocol}\" (already rejected by config validation)");
        }
    }

    private static McMap McMapOf(ServerMapConfig m, IDeviceAccess device)
    {
        if (!McDevice.TryFromCode(m.Device, out var dev))
        {
            throw new InvalidOperationException($"invalid MC device code \"{m.Device}\" (already rejected by config validation)");
        }
        var table = LocalKinds.ToTable(m.Kind);
        return new McMap(dev, table, m.Offset, m.Count ?? device.Length(table) - m.Offset);
    }

    private static FinsMap FinsMapOf(ServerMapConfig m, IDeviceAccess device)
    {
        if (!FinsArea.TryFromCode(m.Device, out var area))
        {
            throw new InvalidOperationException($"invalid FINS area \"{m.Device}\" (already rejected by config validation)");
        }
        var table = LocalKinds.ToTable(m.Kind);
        return new FinsMap(area, table, m.Offset, m.Count ?? device.Length(table) - m.Offset);
    }

    private static McEntry McEntryOf(string remote, int count, TableKind table, int localOffset) =>
        McDevice.TryParse(remote, out var device, out var address)
            ? new McEntry(device, address, count, table, localOffset)
            : throw new InvalidOperationException($"invalid MC device \"{remote}\" (already rejected by config validation)");

    /// <summary>Resolve a validated host:port client remote into an endpoint.</summary>
    private static IPEndPoint ParseRemoteAddr(string addr)
    {
        var sep = addr.LastIndexOf(':');
        var host = addr[..sep];
        var port = int.Parse(addr[(sep + 1)..]);
        if (IPAddress.TryParse(host, out var ip))
        {
            return new IPEndPoint(ip, port);
        }
        // hostname: resolve once at startup (a client that must survive DNS
        // changes would re-resolve per cycle — only add that when needed)
        var addresses = System.Net.Dns.GetHostAddresses(host);
        return new IPEndPoint(addresses.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), port);
    }

    /// <summary>Open the serial port backing an RTU server or master; the Core layer only ever sees the stream.</summary>
    private static Stream OpenSerialPort(string name, int baud, string parity, int dataBits, int stopBits)
    {
        var port = new System.IO.Ports.SerialPort(name, baud, ToParity(parity), dataBits, (System.IO.Ports.StopBits)stopBits)
        {
            ReadTimeout = System.Threading.Timeout.Infinite, // the RTU loop owns timing
            WriteTimeout = 1000,
        };
        port.Open();
        return port.BaseStream;
    }

    private static System.IO.Ports.Parity ToParity(string parity) => parity switch
    {
        "none" => System.IO.Ports.Parity.None,
        "even" => System.IO.Ports.Parity.Even,
        "odd" => System.IO.Ports.Parity.Odd,
        _ => throw new ArgumentException($"invalid parity \"{parity}\" (already rejected by config validation)"),
    };

    private static LogLevel ToLevel(string level) => level switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Information,
        "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Information,
    };
}