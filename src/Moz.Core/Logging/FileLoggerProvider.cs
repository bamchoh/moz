using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moz.Logging;

/// <summary>
/// Minimal append-only JSON-lines file sink (~80 lines). The CLI attaches it
/// when config requests a log file; the future GUI does not have to know it
/// exists. Single writer thread assumption: the provider serializes writes
/// with a lock. Files are appended, never rotated (anti-scope for now).
/// </summary>
public sealed class FileLoggerProvider(string filePath) : ILoggerProvider
{
    private readonly Lock _sync = new();
    private StreamWriter? _writer;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var line = new
            {
                ts = DateTime.UtcNow.ToString("o"),
                level = logLevel.ToString().ToLowerInvariant(),
                category,
                eventId = eventId.Id,
                message = formatter(state, exception),
                values = AsPairs(state),
            };
            var json = JsonSerializer.Serialize(line, MozJson.Options);
            owner.Write(json);
        }

        private static Dictionary<string, object?>? AsPairs(object? state) =>
            state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : null;
    }

    private void Write(string json)
    {
        lock (_sync)
        {
            _writer ??= new StreamWriter(
                new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                Encoding.UTF8) { AutoFlush = true };
            _writer.WriteLine(json);
        }
    }
}

/// <summary>Shared JSON options for log lines: compact, ISO timestamps inside payloads.</summary>
internal static class MozJson
{
    public static readonly JsonSerializerOptions Options = new();
}