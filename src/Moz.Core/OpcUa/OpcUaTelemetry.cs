using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Opc.Ua;

namespace Moz.OpcUa;

/// <summary>
/// SDK telemetry plumbing (2.0.1 style). No server-side console spam by
/// default: moz's structured logs are the user-facing surface. SDK internals
/// are captured into <see cref="SdkLog"/> for diagnosability/tests.
/// </summary>
public static class OpcUaTelemetry
{
    private static readonly Lazy<ITelemetryContext> s_instance = new(() =>
    {
        var context = Opc.Ua.DefaultTelemetry.Create(b => b.AddProvider(new CollectorProvider()));
        return context;
    });

    private static readonly ConcurrentQueue<(string Level, string Message)> s_sdkLog = new();

    public static ITelemetryContext Telemetry => s_instance.Value;

    /// <summary>SDK-internal log entries (capped ring behavior by trimming).</summary>
    public static IReadOnlyList<(string Level, string Message)> SdkLog => s_sdkLog.ToArray();

    public static void SdkLogClear() => s_sdkLog.Clear();

    private sealed class CollectorProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CollectorLogger(categoryName);

        internal IList<string> SnapshotCategories() { return []; } // keep category name wiring minimal
        public void Dispose() { }
    }

    private sealed class CollectorLogger(string category) : ILogger
    {

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            while (s_sdkLog.Count > 400)
            {
                s_sdkLog.TryDequeue(out _);
            }
            s_sdkLog.Enqueue((logLevel.ToString(), $"{category}: {formatter(state, exception)}"));
        }
    }
}