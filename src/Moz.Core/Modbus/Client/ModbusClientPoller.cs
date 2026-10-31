using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Modbus.Client;

/// <summary>One poll line: read [remote_offset, +count) with fc, write into local table at local_offset.</summary>
public readonly record struct PollEntry(int Fc, int RemoteOffset, int Count, TableKind LocalTable, int LocalOffset);

/// <summary>
/// moz as Modbus TCP master: connects to a remote slave and mirrors reads
/// into a local device on a fixed interval. One persistent connection; every
/// error (connect, timeout, exception response) is logged and the loop
/// retries next tick — never fatal. v1 mirrors read-only (writing back to
/// the remote arrives in a later phase).
/// </summary>
public sealed class ModbusClientPoller : IProtocolRunner
{
    private readonly IPEndPoint _remote;
    private readonly byte _remoteUnit;
    private readonly int _intervalMs;
    private readonly int _timeoutMs;
    private readonly Device _local;
    private readonly PollEntry[] _polls;
    private readonly ILogger _logger;

    private TcpClient? _client;
    private ushort _transactionId;

    public ModbusClientPoller(string name, IPEndPoint remote, byte remoteUnit,
        int intervalMs, int timeoutMs, Device local, PollEntry[] polls, ILogger logger)
    {
        Name = name;
        _remote = remote;
        _remoteUnit = remoteUnit;
        _intervalMs = intervalMs;
        _timeoutMs = timeoutMs;
        _local = local;
        _polls = polls;
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>True while connected (observability for tests/future GUI).</summary>
    public bool IsConnected => _client is { Connected: true };

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await RunCycleAsync(ct);
                await Task.Delay(_intervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    /// <summary>One interval's worth of work: (re)connect lazily, run all polls.</summary>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        var stream = await ConnectAsync(ct);
        if (stream is null)
        {
            return; // connect failure already logged
        }
        foreach (var poll in _polls)
        {
            try
            {
                await MirrorPollAsync(stream, poll, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // any bad response poisons the pipelining state: drop the connection, retry next tick
                _logger.ConnError(Name, _remote.ToString(), ex.Message);
                Close();
                return;
            }
        }
    }

    /// <summary>Read one poll entry from the remote and write it into the local device.</summary>
    internal async Task MirrorPollAsync(NetworkStream stream, PollEntry poll, CancellationToken ct)
    {
        var req = ModbusClient.BuildReadRequest(poll.Fc, poll.RemoteOffset, poll.Count);

        var tid = ++_transactionId;
        var frame = new byte[Mbap.HeaderSize + req.Length];
        Mbap.WriteHeader(frame, tid, _remoteUnit, req.Length);
        req.CopyTo(frame.AsSpan(Mbap.HeaderSize));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);
        var token = timeoutCts.Token;

        try
        {
            await stream.WriteAsync(frame, token);

            var header = new byte[Mbap.HeaderSize];
            var resp = new byte[ModbusLimits.PduMaxLength + 2];
            await stream.ReadExactlyAsync(header, token);
            if (!Mbap.TryParseHeader(header, out var mbap))
            {
                throw new IOException("remote sent a malformed MBAP header");
            }
            if (mbap.TransactionId != tid)
            {
                throw new IOException($"response transaction id {mbap.TransactionId} ≠ request {tid}");
            }
            await stream.ReadExactlyAsync(resp.AsMemory(0, mbap.Length - 1), token);

            var pdu = resp.AsSpan(0, mbap.Length - 1);
            if (mbap.UnitId != _remoteUnit)
            {
                _logger.MalformedFrame(Name, _remote.ToString(), $"response unit id {mbap.UnitId} ≠ {_remoteUnit}");
                return; // no answer to mirror, stay on the connection
            }
            CopyToLocal(poll, pdu);

            _logger.Request(Name, _remoteUnit, (byte)poll.Fc,
                RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
                IsException(pdu) ? ExceptionCodeOf(pdu) : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"poll timed out after {_timeoutMs}ms");
        }
    }

    /// <summary>Parse + apply to the local device (one Write ⇒ one DeviceChange / variable log line).</summary>
    private void CopyToLocal(PollEntry poll, ReadOnlySpan<byte> pdu)
    {
        if (IsException(pdu))
        {
            return; // logged as exc=… by the Request line; the local device stays untouched
        }
        if (poll.Fc is 3 or 4)
        {
            var words = ModbusClient.ParseWordResponse(poll.Fc, poll.Count, pdu);
            _local.Write(poll.LocalTable, poll.LocalOffset, words);
            return;
        }
        var bits = ModbusClient.ParseBitResponse(poll.Fc, poll.Count, pdu);
        _local.Write(poll.LocalTable, poll.LocalOffset, bits);
    }

    private static bool IsException(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 1 && (pdu[0] & 0x80) != 0;

    private static byte ExceptionCodeOf(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 2 ? pdu[1] : (byte)0;

    // --- connection -----------------------------------------------------------

    /// <summary>Lazy persistent connection; a failed connect is logged once per cycle.</summary>
    private async Task<NetworkStream?> ConnectAsync(CancellationToken ct)
    {
        if (IsConnected)
        {
            return _client!.GetStream();
        }
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(_remote.Address, _remote.Port, ct);
            client.ReceiveTimeout = _timeoutMs;
            client.SendTimeout = _timeoutMs;
            _client = client;
            _logger.ConnAccepted(Name, _remote.ToString());
            return client.GetStream();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.ConnError(Name, _remote.ToString(), ex.Message);
            return null;
        }
    }

    private void Close()
    {
        _client?.Dispose();
        _client = null;
    }
}