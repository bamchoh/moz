using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Moz;
using Moz.Config;
using Moz.Devices;
using Moz.Logging;
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
                coils: d.Coils,
                discreteInputs: d.Discrete,
                holdingRegisters: d.Holding,
                inputRegisters: d.Input);
        }

        // typed variable overlays (phase 3): per-device change logging +
        // the named access surface scripts and the OPC UA server use
        var variableTables = new Dictionary<string, VariableTable>();
        var structs = ConfigLoader.ToStructDefs(config);
        foreach (var d in config.Device)
        {
            // struct-typed declarations expand into dotted leaf variables here;
            // the VariableTable (and everything above it) only sees leaves
            var variables = config.Variable.Where(v => v.Device == d.Name).SelectMany(v =>
                VariableLayout.Expand(v.Name, v.ToTableKind(), v.Offset, v.Type, v.Count, v.Length ?? 0, structs));
            variableTables[d.Name] = new VariableTable(
                devices[d.Name], variables, loggerFactory!.CreateLogger($"moz.device.{d.Name}"));
        }

        var runners = new List<IProtocolRunner>();

        // client pollers (phase 6): moz as master mirrors remote slaves into local
        // devices (poll) and writes local changes back (push). Each poll/push range
        // is also recorded as a ClientLink so scripts can ask plc.getVariableStatus
        var links = new Dictionary<string, List<ClientLink>>();
        foreach (var c in config.Client)
        {
            var poller = new ModbusClientPoller(
                c.Name,
                ParseRemoteAddr(c.RemoteAddr),
                (byte)c.RemoteUnit,
                c.IntervalMs,
                c.TimeoutMs,
                devices[c.Device],
                c.Poll.Select(p => new PollEntry(p.Fc, p.RemoteOffset, p.Count, p.EffectiveLocalTable(), p.LocalOffset)).ToArray(),
                loggerFactory.CreateLogger($"moz.client.{c.Name}"),
                c.Push.Select(p => new PushEntry(p.Fc, p.EffectiveLocalTable(), p.LocalOffset, p.Count, p.RemoteOffset)).ToArray());
            runners.Add(poller);
            var deviceLinks = links.TryGetValue(c.Device, out var l) ? l : links[c.Device] = [];
            deviceLinks.AddRange(c.Poll.Select(p => new ClientLink(p.EffectiveLocalTable(), p.LocalOffset, p.Count, () => poller.IsConnected)));
            deviceLinks.AddRange(c.Push.Select(p => new ClientLink(p.EffectiveLocalTable(), p.LocalOffset, p.Count, () => poller.IsConnected)));
        }

        // periodic scripts (phase 4): paths resolve relative to the config file
        var configDir = Path.GetDirectoryName(Path.GetFullPath(result.File))!;
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
            runners.Add(s.Protocol switch
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
                    loggerFactory.CreateLogger($"moz.server.{s.Name}")),
                "modbus-rtu" => new RtuServer(
                    s.Name,
                    OpenSerialPort(s),
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

        var tasks = runners.Select(r => r.RunAsync(cts.Token)).ToArray();
        logger.LogInformation("starting {Count} runner(s)", runners.Count);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            // one runner failed fatally — propagate, report, exit
            logger.LogError(ex, "runner failed: {Message}", ex.Message);
            await cts.CancelAsync();
            try { await Task.WhenAll(tasks); } catch { /* propagate first failure only */ }
            return 1;
        }

        logger.LogInformation("all runners stopped");
        return 0;
    }

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

    /// <summary>Open the serial port backing an RTU runner; the Core layer only ever sees the stream.</summary>
    private static Stream OpenSerialPort(ServerConfig s)
    {
        var port = new System.IO.Ports.SerialPort(
            s.SerialPort!, s.Baud, ToParity(s.Parity), s.DataBits,
            (System.IO.Ports.StopBits)s.StopBits)
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