using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Moz;
using Moz.Config;
using Moz.Devices;
using Moz.Logging;
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
        // the named access surface future scripts / GUI / OPC UA will use
        var variableTables = new Dictionary<string, VariableTable>();
        foreach (var d in config.Device)
        {
            var variables = config.Variable.Where(v => v.Device == d.Name).Select(v =>
                new Variable(v.Name, v.ToTableKind(), v.Offset, v.ToVariableType(), v.Count));
            variableTables[d.Name] = new VariableTable(
                devices[d.Name], variables, loggerFactory!.CreateLogger($"moz.device.{d.Name}"));
        }

        var runners = new List<IProtocolRunner>();

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
                loggerFactory.CreateLogger($"moz.script.{s.Name}")));
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