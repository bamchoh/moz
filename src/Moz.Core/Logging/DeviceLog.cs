using Microsoft.Extensions.Logging;

namespace Moz.Logging;

/// <summary>
/// Device-level observation events (not communication): typed named
/// variables changing, and later phases' device-facing events. Same
/// structured-state pattern as <see cref="CommLog"/>.
/// </summary>
public static class DeviceLog
{
    public static class EventIds
    {
        /// <summary>A named variable's value changed (one line per mutation batch).</summary>
        public static readonly EventId VarChange = new(1101, "VariableChange");
    }

    /// <summary>Text line: "plc-a temperature=12.5" or "plc-a flags=[true,false,true]".</summary>
    public static void VarChange(this ILogger log, string device, string name, string value)
    {
        if (!log.IsEnabled(LogLevel.Information))
        {
            return;
        }
        log.Log(LogLevel.Information, EventIds.VarChange, new VarChangeState(device, name, value), null,
            static (s, _) => s?.ToString() ?? string.Empty);
    }

    private sealed class VarChangeState(string device, string name, string value) : LogState
    {
        private readonly KeyValuePair<string, object?>[] _items =
        [
            new("device", device),
            new("name", name),
            new("value", value),
        ];

        public override string ToString() => $"{device} {name}={value}";
        protected override KeyValuePair<string, object?>[] Items => _items;
    }
}