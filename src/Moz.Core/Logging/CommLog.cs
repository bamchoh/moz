using System.Collections;
using Microsoft.Extensions.Logging;

namespace Moz.Logging;

/// <summary>
/// Central registry of communication EventIds and emission helpers.
/// Structured state classes render both the humane text line (via ToString)
/// and the JSON key/value pairs (via the IReadOnlyList implementation).
/// Levels: Request=Debug, accept/close=Information, error/malformed=Warning,
/// listener failure=Error. The future GUI subscribes by adding an
/// <see cref="ILoggerProvider"/>.
/// </summary>
public static class CommLog
{
    /// <summary>EventIds, kept in a nested class so method names stay free.</summary>
    public static class EventIds
    {
        public static readonly EventId Request = new(1001, "Request");
        public static readonly EventId ConnAccepted = new(2001, "ConnectionAccepted");
        public static readonly EventId ConnClosed = new(2002, "ConnectionClosed");
        public static readonly EventId ConnError = new(2003, "ConnectionError");
        public static readonly EventId MalformedFrame = new(2004, "MalformedFrame");
        public static readonly EventId ListenFailed = new(3000, "ListenFailed");
    }

    /// <summary>One handled Modbus request. exc==null means a normal response.</summary>
    public static void Request(this ILogger log, string conn, byte unit, byte fc,
        ushort addr, ushort qty, byte? exc) =>
        log.LogState(LogLevel.Debug, EventIds.Request, new RequestState(conn, unit, fc, addr, qty, exc));

    /// <summary>One client request on a non-Modbus protocol: op (read/write), target ("D100"), points; error==null means success.</summary>
    public static void Request(this ILogger log, string conn, string op, string target, int qty, string? error) =>
        log.LogState(LogLevel.Debug, EventIds.Request, new RemoteRequestState(conn, op, target, qty, error));

    public static void ConnAccepted(this ILogger log, string conn, string remote) =>
        log.LogState(LogLevel.Information, EventIds.ConnAccepted, new ConnState(conn, remote));

    public static void ConnClosed(this ILogger log, string conn, string remote) =>
        log.LogState(LogLevel.Information, EventIds.ConnClosed, new ConnState(conn, remote));

    public static void ConnError(this ILogger log, string conn, string remote, string error) =>
        log.LogState(LogLevel.Warning, EventIds.ConnError, new ConnErrorState(conn, remote, error));

    public static void MalformedFrame(this ILogger log, string conn, string remote, string error) =>
        log.LogState(LogLevel.Warning, EventIds.MalformedFrame, new ConnErrorState(conn, remote, error));

    public static void ListenFailed(this ILogger log, string endpoint, string error)
    {
        if (!log.IsEnabled(LogLevel.Error))
        {
            return;
        }
        log.Log(LogLevel.Error, EventIds.ListenFailed, new ListenFailedState(endpoint, error), null,
            static (s, _) => s?.ToString() ?? string.Empty);
    }

    // --- helpers ------------------------------------------------------------

    private static void LogState(this ILogger log, LogLevel level, EventId id,
        IReadOnlyList<KeyValuePair<string, object?>> state)
    {
        if (!log.IsEnabled(level))
        {
            return;
        }
        log.Log(level, id, state, null, TextFormatter);
    }

    // MEL formatters have the shape Func<TState, Exception?, string>.
    private static string TextFormatter(object? state, object? _) =>
        state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
            : state?.ToString() ?? string.Empty;

    // --- state classes ------------------------------------------------------

    private sealed class RequestState(string conn, byte unit, byte fc, ushort addr, ushort qty, byte? exc)
        : LogState
    {
        private readonly KeyValuePair<string, object?>[] _items = exc is null
            ?
            [
                new("conn", conn),
                new("unit", unit),
                new("fc", $"{fc:X2}"),
                new("addr", addr),
                new("qty", qty),
                new("result", "ok"),
            ]
            :
            [
                new("conn", conn),
                new("unit", unit),
                new("fc", $"{fc:X2}"),
                new("addr", addr),
                new("qty", qty),
                new("exc", exc.Value.ToString()),
            ];

        public override string ToString() => exc is null
            ? $"conn={conn} unit={unit} fc={fc:X2} addr={addr} qty={qty} result=ok"
            : $"conn={conn} unit={unit} fc={fc:X2} addr={addr} qty={qty} exc={exc}";
        protected override KeyValuePair<string, object?>[] Items => _items;
    }

    private sealed class RemoteRequestState(string conn, string op, string target, int qty, string? error) : LogState
    {
        private readonly KeyValuePair<string, object?>[] _items =
        [
            new("conn", conn),
            new("op", op),
            new("target", target),
            new("qty", qty),
            error is null ? new("result", "ok") : new("err", error),
        ];

        public override string ToString() => error is null
            ? $"conn={conn} op={op} target={target} qty={qty} result=ok"
            : $"conn={conn} op={op} target={target} qty={qty} err={error}";
        protected override KeyValuePair<string, object?>[] Items => _items;
    }

    private sealed class ConnState(string conn, string remote) : LogState
    {
        private readonly KeyValuePair<string, object?>[] _items =
        [
            new("conn", conn),
            new("remote", remote),
        ];

        public override string ToString() => $"conn={conn} remote={remote}";
        protected override KeyValuePair<string, object?>[] Items => _items;
    }

    private sealed class ConnErrorState(string conn, string remote, string error) : LogState
    {
        private readonly KeyValuePair<string, object?>[] _items =
        [
            new("conn", conn),
            new("remote", remote),
            new("err", error),
        ];

        public override string ToString() => $"conn={conn} remote={remote} err={error}";
        protected override KeyValuePair<string, object?>[] Items => _items;
    }

    private sealed class ListenFailedState(string endpoint, string error) : LogState
    {
        private readonly KeyValuePair<string, object?>[] _items =
        [
            new("endpoint", endpoint),
            new("err", error),
        ];

        public override string ToString() => $"endpoint={endpoint} err={error}";
        protected override KeyValuePair<string, object?>[] Items => _items;
    }
}