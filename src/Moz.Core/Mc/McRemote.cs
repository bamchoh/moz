using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Client;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Mc;

/// <summary>One poll or push item: count points of device at address ↔ local table at local_offset.</summary>
public readonly record struct McEntry(McDevice Device, int Address, int Count, TableKind LocalTable, int LocalOffset)
{
    public LocalRange Local => new(LocalTable, LocalOffset, Count);
    public string Target => Device.Format(Address);
}

/// <summary>
/// A MELSEC CPU (or anything speaking MC protocol 3E binary over TCP) as
/// seen by the generic client loop: each poll item is one batch read, each
/// push item one batch write, on one persistent connection with one request
/// in flight (3E has no serial number, so requests are strictly sequential).
/// A non-zero end code is logged (`err=C059`) and treated as a rejection: a
/// poll mirrors nothing, a push is not retried. Timeouts and malformed
/// responses are IOExceptions for the loop to reset (= reconnect) and retry.
/// </summary>
public sealed class McRemote : IRemoteDevice
{
    private readonly string _name;
    private readonly IPEndPoint _remote;
    private readonly int _timeoutMs;
    private readonly Mc3EOptions _options;
    private readonly McEntry[] _polls;
    private readonly McEntry[] _pushes;
    private readonly ILogger _logger;

    private TcpClient? _client;

    public McRemote(string name, IPEndPoint remote, int timeoutMs, Mc3EOptions options,
        McEntry[] polls, McEntry[] pushes, ILogger logger)
    {
        _name = name;
        _remote = remote;
        _timeoutMs = timeoutMs;
        _options = options;
        _polls = polls;
        _pushes = pushes;
        _logger = logger;
        Remote = remote.ToString();
        Polls = polls.Select(p => p.Local).ToArray();
        Pushes = pushes.Select(p => p.Local).ToArray();
    }

    public string Remote { get; }
    public bool IsOpen => _client is { Connected: true };
    public bool IsConnected => IsOpen;
    public IReadOnlyList<LocalRange> Polls { get; }
    public IReadOnlyList<LocalRange> Pushes { get; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (IsOpen)
        {
            return;
        }
        var client = new TcpClient();
        await client.ConnectAsync(_remote.Address, _remote.Port, ct);
        client.ReceiveTimeout = _timeoutMs;
        client.SendTimeout = _timeoutMs;
        _client = client;
    }

    public async Task<bool[]?> ReadBitsAsync(int poll, CancellationToken ct)
    {
        var e = _polls[poll];
        var body = await ExchangeAsync("read", e, Mc3E.BuildReadRequest(_options, e.Device, e.Address, e.Count), ct);
        return body is null ? null : Mc3E.ParseBits(body, e.Count);
    }

    public async Task<ushort[]?> ReadWordsAsync(int poll, CancellationToken ct)
    {
        var e = _polls[poll];
        var body = await ExchangeAsync("read", e, Mc3E.BuildReadRequest(_options, e.Device, e.Address, e.Count), ct);
        return body is null ? null : Mc3E.ParseWords(body, e.Count);
    }

    public async Task WriteBitsAsync(int push, bool[] values, CancellationToken ct)
    {
        var e = _pushes[push];
        var body = await ExchangeAsync("write", e, Mc3E.BuildWriteRequest(_options, e.Device, e.Address, values), ct);
        CheckWriteBody(body);
    }

    public async Task WriteWordsAsync(int push, ushort[] values, CancellationToken ct)
    {
        var e = _pushes[push];
        var body = await ExchangeAsync("write", e, Mc3E.BuildWriteRequest(_options, e.Device, e.Address, values), ct);
        CheckWriteBody(body);
    }

    public Task ResetAsync(CancellationToken ct)
    {
        Close();
        return Task.CompletedTask;
    }

    public void Close()
    {
        _client?.Dispose();
        _client = null;
    }

    private static void CheckWriteBody(byte[]? body)
    {
        if (body is { Length: > 2 })
        {
            throw new IOException($"unexpected data in a write response ({body.Length - 2} bytes)");
        }
    }

    /// <summary>
    /// One request/response. Returns the response body (end code + data) on
    /// success, null when the remote answered with a non-zero end code
    /// (logged). Throws IOException on timeout or a malformed response.
    /// </summary>
    private async Task<byte[]?> ExchangeAsync(string op, McEntry entry, byte[] request, CancellationToken ct)
    {
        var stream = (_client ?? throw new InvalidOperationException("link is not open")).GetStream();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        var token = timeoutCts.Token;
        byte[] body;
        try
        {
            await stream.WriteAsync(request, token);
            var header = new byte[Mc3E.HeaderSize];
            await stream.ReadExactlyAsync(header, token);
            if (!Mc3E.TryParseResponseHeader(header, out var length))
            {
                throw new IOException($"malformed MC response header: {Convert.ToHexString(header)}");
            }
            body = new byte[length];
            await stream.ReadExactlyAsync(body, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"request timed out after {_timeoutMs}ms");
        }
        catch (EndOfStreamException)
        {
            throw new IOException("remote closed the connection mid-response");
        }
        var endCode = Mc3E.EndCode(body);
        _logger.Request(_name, op, entry.Target, entry.Count, endCode == 0 ? null : $"{endCode:X4}");
        return endCode == 0 ? body : null;
    }
}
