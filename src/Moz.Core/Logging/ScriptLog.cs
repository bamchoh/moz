using Microsoft.Extensions.Logging;

namespace Moz.Logging;

/// <summary>
/// Script-level observation events: console.log output. Text form is
/// "script [name] <message>" — a stable prefix for grep, consistent with
/// CommLog/DeviceLog's line style.
/// </summary>
public static class ScriptLog
{
    public static class EventIds
    {
        public static readonly EventId ConsoleLog = new(2101, "ConsoleLog");
        public static readonly EventId Error = new(2102, "ScriptError");
    }

    public static void Message(ILogger log, string scriptName, string message)
    {
        if (!log.IsEnabled(LogLevel.Information))
        {
            return;
        }
        log.Log(LogLevel.Information, EventIds.ConsoleLog,
            new ScriptLogState(scriptName, message), null, static (s, _) => s?.ToString() ?? string.Empty);
    }

    public static void Error(ILogger log, string scriptName, string error)
    {
        if (!log.IsEnabled(LogLevel.Warning))
        {
            return;
        }
        log.Log(LogLevel.Warning, EventIds.Error,
            new ScriptLogState(scriptName, error), null, static (s, _) => s?.ToString() ?? string.Empty);
    }

    private sealed class ScriptLogState(string scriptName, string message) : LogState
    {
        private readonly KeyValuePair<string, object?>[] _items =
        [
            new("script", scriptName),
            new("message", message),
        ];

        public override string ToString() => $"script [{scriptName}] {message}";
        protected override KeyValuePair<string, object?>[] Items => _items;
    }
}