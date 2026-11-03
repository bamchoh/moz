using System.Net;
using System.Net.Sockets;
using RtuAdu = Moz.Modbus.Rtu.Rtu;

namespace Moz.Modbus.Client;

/// <summary>
/// The transport under the Modbus master (<see cref="ModbusRemote"/>):
/// one request PDU in, one response PDU out, over a link that the poller
/// opens lazily and recovers after errors. TCP (MBAP, persistent socket) and
/// RTU (serial-like Stream, CRC16) implement it; the poll/push loop above is
/// transport-agnostic.
/// </summary>
public interface IMasterLink
{
    /// <summary>Remote description for log lines ("127.0.0.1:502", "COM10").</summary>
    string Remote { get; }

    /// <summary>The link is established (socket connected / port open).</summary>
    bool IsOpen { get; }

    /// <summary>
    /// The remote is reachable: TCP = socket connected; RTU = port open and
    /// the slave answered the last request (a timeout clears it).
    /// </summary>
    bool IsConnected { get; }

    /// <summary>Establish the link (connect / open the port). Throws on failure.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Send one request PDU to <paramref name="unit"/> and return the response
    /// PDU (exception responses included). Throws <see cref="IOException"/> on
    /// timeout, link failure, or a malformed / mismatched response.
    /// </summary>
    Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken ct);

    /// <summary>Recover after an exchange error: TCP drops the connection, RTU discards buffered input.</summary>
    Task ResetAsync(CancellationToken ct);

    /// <summary>Release the link (shutdown).</summary>
    void Close();
}

/// <summary>
/// Modbus TCP master link: one persistent connection; MBAP header with a
/// transaction id that increases per request and must be echoed. Any
/// inconsistency in a response (tid, unit id, header) poisons the pipelining
/// state, so it is an IOException and the poller resets (= reconnects).
/// </summary>
public sealed class TcpMasterLink(IPEndPoint remote, int timeoutMs) : IMasterLink
{
    private TcpClient? _client;
    private ushort _transactionId;

    public string Remote { get; } = remote.ToString();

    public bool IsOpen => _client is { Connected: true };

    public bool IsConnected => IsOpen;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (IsOpen)
        {
            return;
        }
        var client = new TcpClient();
        await client.ConnectAsync(remote.Address, remote.Port, ct);
        client.ReceiveTimeout = timeoutMs;
        client.SendTimeout = timeoutMs;
        _client = client;
    }

    public async Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken ct)
    {
        var stream = (_client ?? throw new InvalidOperationException("link is not open")).GetStream();
        var tid = ++_transactionId;
        var frame = new byte[Mbap.HeaderSize + pdu.Length];
        Mbap.WriteHeader(frame, tid, unit, pdu.Length);
        pdu.CopyTo(frame.AsSpan(Mbap.HeaderSize));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        var token = timeoutCts.Token;
        try
        {
            await stream.WriteAsync(frame, token);

            var header = new byte[Mbap.HeaderSize];
            await stream.ReadExactlyAsync(header, token);
            if (!Mbap.TryParseHeader(header, out var mbap))
            {
                throw new IOException("remote sent a malformed MBAP header");
            }
            if (mbap.TransactionId != tid)
            {
                throw new IOException($"response transaction id {mbap.TransactionId} ≠ request {tid}");
            }
            var resp = new byte[mbap.Length - 1]; // TryParseHeader bounds the length
            await stream.ReadExactlyAsync(resp, token);
            if (mbap.UnitId != unit)
            {
                throw new IOException($"response unit id {mbap.UnitId} ≠ {unit}");
            }
            return resp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"request timed out after {timeoutMs}ms");
        }
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
}

/// <summary>
/// Modbus RTU master link over a serial-like Stream (the CLI opens the serial
/// port; tests use in-memory pipes). Framing: [address] [PDU] [CRC16]. The
/// response length follows from the FC (and byte count) so the reader stops
/// exactly at the frame end; the first byte waits <c>timeoutMs</c>, later
/// bytes <c>idleGapMs</c> (RTU's frame delimiter). A silent slave is a
/// timeout, not a link failure: the port stays open and the poller retries
/// next tick. After any error <see cref="ResetAsync"/> drains stale bytes
/// (a late response) so the next exchange starts clean.
/// </summary>
public sealed class RtuMasterLink(Func<Stream> open, string remote, int timeoutMs, int idleGapMs) : IMasterLink
{
    private const int ByteEof = -2;
    private const int ByteIdle = -1;

    private readonly byte[] _one = new byte[1];
    private readonly byte[] _buf = new byte[RtuAdu.AduMaxSize];
    private Stream? _stream;
    private bool _answered;

    public string Remote { get; } = remote;

    public bool IsOpen => _stream is not null;

    public bool IsConnected => _stream is not null && _answered;

    public Task ConnectAsync(CancellationToken ct)
    {
        _stream ??= open();
        return Task.CompletedTask;
    }

    public async Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("link is not open");
        try
        {
            await stream.WriteAsync(RtuAdu.Frame(unit, pdu), ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int length = 0;
            int expected = RtuAdu.NeedMore;
            while (true)
            {
                int b = await ReadByteAsync(stream, cts, length == 0 ? timeoutMs : idleGapMs, ct);
                if (b == ByteEof)
                {
                    throw new IOException("link closed");
                }
                if (b == ByteIdle)
                {
                    throw length == 0
                        ? new IOException($"request timed out after {timeoutMs}ms")
                        : new IOException($"incomplete response ({length} bytes): {RtuAdu.HexDump(_buf.AsSpan(0, length))}");
                }
                _buf[length++] = (byte)b;
                if (expected == RtuAdu.NeedMore)
                {
                    expected = RtuAdu.ResponseLength(_buf.AsSpan(0, length));
                    if (expected == RtuAdu.Invalid)
                    {
                        throw new IOException($"malformed response: {RtuAdu.HexDump(_buf.AsSpan(0, length))}");
                    }
                }
                if (expected != RtuAdu.NeedMore && length == expected)
                {
                    break;
                }
            }

            var frame = _buf.AsSpan(0, length);
            if (!RtuAdu.CrcOk(frame))
            {
                throw new IOException($"response CRC mismatch: {RtuAdu.HexDump(frame)}");
            }
            if (frame[0] != unit)
            {
                throw new IOException($"response slave address {frame[0]} ≠ {unit}");
            }
            _answered = true;
            return frame[1..^2].ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _answered = false;
            throw;
        }
    }

    /// <summary>Discard whatever arrives until the line is idle (a late response to a timed-out request).</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        _answered = false;
        if (_stream is null)
        {
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (await ReadByteAsync(_stream, cts, idleGapMs, ct) >= 0)
        {
            // draining
        }
    }

    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
        _answered = false;
    }

    /// <summary>One byte within <paramref name="waitMs"/>; <see cref="ByteIdle"/> on timeout, <see cref="ByteEof"/> on link end.</summary>
    private async Task<int> ReadByteAsync(Stream stream, CancellationTokenSource cts, int waitMs, CancellationToken ct)
    {
        cts.CancelAfter(waitMs);
        try
        {
            int n = await stream.ReadAsync(_one, cts.Token);
            return n == 0 ? ByteEof : _one[0];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ByteIdle;
        }
    }
}
