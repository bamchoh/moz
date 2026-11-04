using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.Modbus.Tcp;

namespace Moz.Tests;

/// <summary>Shared test harness: start a server on an ephemeral port and stop it deterministically.</summary>
internal sealed class TestServer(Device device, ushort port, CancellationTokenSource cts, CapturingProvider? provider)
    : IAsyncDisposable
{
    public Device Device { get; } = device;
    public ushort Port { get; } = port;
    public CapturingProvider? Provider { get; } = provider;

    public static async Task<TestServer> Start(Device? device = null, ushort[]? unitIds = null,
        CapturingProvider? provider = null, int? port = null)
    {
        device ??= MakeDefaultDevice();
        var server = new ModbusTcpServer(
            "test", new IPEndPoint(IPAddress.Loopback, port ?? 0), device, unitIds,
            provider is null ? NullLogger.Instance : provider.CreateLogger("test"));
        var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        while (server.BoundEndpoint is null)
        {
            await Task.Delay(10);
            if (run.IsFaulted)
            {
                await run;
            }
        }
        return new TestServer(device, (ushort)((IPEndPoint)server.BoundEndpoint).Port, cts, provider);
    }

    /// <summary>The standard seeded device: known values so read tests need no setup.</summary>
    public static Device MakeDefaultDevice()
    {
        var device = new Device("t", bits: 2000, readOnlyBits: 2000, words: 200, readOnlyWords: 200);
        device.Seed(TableKind.Words, 0, new ushort[] { 0x1234, 0xABCD });
        device.Seed(TableKind.ReadOnlyBits, 0, new bool[] { true, false, true });
        device.Seed(TableKind.ReadOnlyWords, 0, new ushort[] { 0xBEEF });
        return device;
    }

    public async ValueTask DisposeAsync()
    {
        await cts.CancelAsync();
        cts.Token.WaitHandle.WaitOne(3000);
        cts.Dispose();
    }
}

/// <summary>Raw TCP client for frame-level policy tests (no Modbus client library involved).</summary>
internal sealed class RawClient(TestServer server) : IDisposable
{
    private readonly System.Net.Sockets.TcpClient _tcp = new();
    private readonly byte[] _buf = new byte[300];

    public NetworkStream Stream { get; private set; } = null!;

    public async Task ConnectAsync()
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        Stream = _tcp.GetStream();
    }

    public Task WriteAsync(byte[] frame, CancellationToken ct) => Stream.WriteAsync(frame, ct).AsTask();

    /// <summary>Read whatever the server sends within the token's window. Empty array = clean close (FIN);
    /// IOException/SocketException = reset by peer.</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var n = await Stream.ReadAsync(_buf, ct);
        return _buf[..n].ToArray();
    }

    public void Dispose() => _tcp.Dispose();
}

/// <summary>Captures log entries in memory (test subscription of the comm log).</summary>
sealed class CapturingProvider : ILoggerProvider
{
    public sealed record Captured(LogLevel Level, int EventId, string Message);

    public readonly List<Captured> Entries = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
    public void Dispose() { }

    private sealed class CapturingLogger(List<Captured> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Add(new Captured(logLevel, eventId.Id, formatter(state, exception)));
    }
}

/// <summary>Two connected in-memory streams standing in for a serial bus (test only).</summary>
internal sealed class SerialBus : IAsyncDisposable
{
    private readonly PipeStream _master;
    private readonly PipeStream _slave;

    public SerialBus()
    {
        (_master, _slave) = PipeStream.CreatePair();
        MasterStream = _master;
        SlaveStream = _slave;
    }

    public PipeStream MasterStream { get; }

    public PipeStream SlaveStream { get; }

    public ValueTask DisposeAsync()
    {
        _master.Dispose();
        _slave.Dispose();
        return ValueTask.CompletedTask;
    }
}