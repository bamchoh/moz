using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Moz;
using Moz.Config;
using Moz.Devices;
using Moz.Logging;
using Moz.Modbus.Tcp;

// moz — PLC communication simulator, CLI entry point.
// Usage: moz <config.toml>        start all servers from the config
// (a `moz validate` subcommand arrives in a later phase; the loader is
// already built so that the same code path runs validation).

if (args.Length != 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: moz <config.toml>");
    return 2;
}

var configPath = args[0];
var result = ConfigLoader.Load(configPath);
if (!result.Ok)
{
    Console.Error.WriteLine($"config has {result.Errors.Count} problem(s):");
    foreach (var error in result.Errors)
    {
        Console.Error.WriteLine($"  {error}");
    }
    return 2;
}
var config = result.Config!;

// --- logging: the CLI owns sinks; libraries only receive ILogger ---------

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(ToLevel(config.Log?.Level ?? "info"));
    builder.AddConsole(o =>
    {
        o.FormatterName = (config.Log?.Format ?? "text") == "json"
            ? ConsoleFormatterNames.Json
            : ConsoleFormatterNames.Simple;
    });
    if (!string.IsNullOrWhiteSpace(config.Log?.File))
    {
        builder.AddProvider(new FileLoggerProvider(config.Log.File));
    }
});
var logger = loggerFactory.CreateLogger("moz");

// --- wire config to implementations: the ONE factory switch ---------------

var devices = new Dictionary<string, Device>();
foreach (var d in config.Device)
{
    devices[d.Name] = new Device(
        d.Name,
        coils: d.Coils ?? 65536,
        discreteInputs: d.Discrete ?? 65536,
        holdingRegisters: d.Holding ?? 65536,
        inputRegisters: d.Input ?? 65536);
}

var runners = new List<IProtocolRunner>();
foreach (var s in config.Server)
{
    var device = devices[s.Device];
    runners.Add(s.Protocol switch
    {
        "modbus-tcp" => new ModbusTcpServer(
            s.Name,
            new IPEndPoint(IPAddress.Parse(s.Bind!), s.Port!.Value),
            device,
            s.UnitIds is { Count: > 0 } ? s.UnitIds.Select(u => (ushort)u).ToArray() : null,
            loggerFactory.CreateLogger($"moz.server.{s.Name}")),
        _ => throw new InvalidOperationException($"unknown protocol \"{s.Protocol}\" (already rejected by config validation)"),
    });
}

// --- run everything; Ctrl+C / process kill stops cleanly ------------------

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

static LogLevel ToLevel(string level) => level switch
{
    "trace" => LogLevel.Trace,
    "debug" => LogLevel.Debug,
    "info" => LogLevel.Information,
    "warn" => LogLevel.Warning,
    "error" => LogLevel.Error,
    _ => LogLevel.Information,
};