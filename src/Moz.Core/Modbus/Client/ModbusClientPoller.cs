using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Modbus.Client;

/// <summary>One poll line: read [remote_offset, +count) with fc, write into local table at local_offset.</summary>
public readonly record struct PollEntry(int Fc, int RemoteOffset, int Count, TableKind LocalTable, int LocalOffset);

/// <summary>One push line: write local [local_offset, +count) to the remote at remote_offset with fc (5/6/15/16).</summary>
public readonly record struct PushEntry(int Fc, TableKind LocalTable, int LocalOffset, int Count, int RemoteOffset);

/// <summary>
/// moz as Modbus TCP master: connects to a remote slave, mirrors reads into a
/// local device on a fixed interval (polls) and writes local changes back to
/// the remote (pushes). One persistent connection; every error (connect,
/// timeout, exception response) is logged and the loop retries next tick —
/// never fatal.
///
/// Push policy: at startup the remote is the truth. Each push entry first
/// READS its remote range into the local device (startup sync; local writes
/// made before that are not pushed and are overwritten). From then on every
/// local change (<see cref="IDeviceAccess.Watch"/> marks the entry dirty and
/// wakes the loop) is written to the remote. Changes the poller itself makes
/// (startup sync, applying a poll) never trigger a push, so the same cells
/// may be polled and pushed (bidirectional mirror) without an echo loop. A
/// communication error keeps the entry dirty (retried next tick); an
/// exception response from the remote is logged and the entry is considered
/// delivered (an exception on the startup read leaves the entry unsynced:
/// retried next tick, never pushed meanwhile).
/// </summary>
public sealed class ModbusClientPoller : IProtocolRunner
{
    private readonly IPEndPoint _remote;
    private readonly byte _remoteUnit;
    private readonly int _intervalMs;
    private readonly int _timeoutMs;
    private readonly Device _local;
    private readonly PollEntry[] _polls;
    private readonly PushEntry[] _pushes;
    private readonly int[] _synced;                       // per push entry: 1 = remote range read into local once (pushes enabled)
    private readonly int[] _dirty;                        // per push entry: 1 = local changed since last delivery
    private readonly SemaphoreSlim _wake = new(0, 1);     // released by a local change to cut the interval wait short
    private readonly ILogger _logger;

    private TcpClient? _client;
    private ushort _transactionId;
    private int _selfWriteThread = -1;                    // thread applying remote data to the local device (its Watch callbacks are ours)

    public ModbusClientPoller(string name, IPEndPoint remote, byte remoteUnit,
        int intervalMs, int timeoutMs, Device local, PollEntry[] polls, ILogger logger, PushEntry[]? pushes = null)
    {
        Name = name;
        _remote = remote;
        _remoteUnit = remoteUnit;
        _intervalMs = intervalMs;
        _timeoutMs = timeoutMs;
        _local = local;
        _polls = polls;
        _pushes = pushes ?? [];
        _synced = new int[_pushes.Length];
        _dirty = new int[_pushes.Length];
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>True while connected (observability for tests/future GUI).</summary>
    public bool IsConnected => _client is { Connected: true };

    /// <summary>True while at least one push entry has an undelivered local change.</summary>
    public bool HasPendingPush
    {
        get
        {
            for (int i = 0; i < _dirty.Length; i++)
            {
                if (Volatile.Read(ref _dirty[i]) != 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var watches = new List<IDisposable>(_pushes.Length);
        try
        {
            for (int i = 0; i < _pushes.Length; i++)
            {
                int index = i;
                var push = _pushes[i];
                watches.Add(_local.Watch(push.LocalTable, push.LocalOffset, push.Count, _ => OnLocalChange(index)));
            }

            long nextPoll = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                bool pollDue = Environment.TickCount64 >= nextPoll;
                if (pollDue)
                {
                    nextPoll = Environment.TickCount64 + _intervalMs;
                }
                await RunCycleAsync(pollDue, ct);
                int wait = (int)Math.Clamp(nextPoll - Environment.TickCount64, 1, _intervalMs);
                await _wake.WaitAsync(wait, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            foreach (var w in watches)
            {
                w.Dispose();
            }
            Close();
        }
    }

    /// <summary>Watch callback: ignore our own writes and anything before the startup sync; otherwise mark dirty and wake the loop.</summary>
    private void OnLocalChange(int index)
    {
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _selfWriteThread)
            || Volatile.Read(ref _synced[index]) == 0)
        {
            return;
        }
        Volatile.Write(ref _dirty[index], 1);
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // another change already woke the loop
            }
        }
    }

    /// <summary>One cycle: (re)connect lazily, startup-sync unsynced pushes, deliver dirty pushes, then (when due) run all polls.</summary>
    private async Task RunCycleAsync(bool pollDue, CancellationToken ct)
    {
        var stream = await ConnectAsync(ct);
        if (stream is null)
        {
            return; // connect failure already logged
        }
        for (int i = 0; i < _pushes.Length; i++)
        {
            if (Volatile.Read(ref _synced[i]) != 0)
            {
                continue;
            }
            try
            {
                await SyncFromRemoteAsync(stream, i, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ConnError(Name, _remote.ToString(), ex.Message);
                Close();
                return;
            }
        }
        for (int i = 0; i < _pushes.Length; i++)
        {
            if (Volatile.Read(ref _synced[i]) == 0 || Volatile.Read(ref _dirty[i]) == 0)
            {
                continue;
            }
            Volatile.Write(ref _dirty[i], 0); // cleared before reading the local values: a later change re-dirties
            try
            {
                await PushAsync(stream, _pushes[i], ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _dirty[i], 1); // undelivered: retry next tick
                _logger.ConnError(Name, _remote.ToString(), ex.Message);
                Close();
                return;
            }
        }
        if (!pollDue)
        {
            return;
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
        var pdu = await ExchangeAsync(stream, req, ct);
        if (pdu is null)
        {
            return;
        }
        if (!IsException(pdu))
        {
            ApplyToLocal(poll.Fc, poll.Count, pdu, poll.LocalTable, poll.LocalOffset);
        }
        _logger.Request(Name, _remoteUnit, (byte)poll.Fc,
            RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
            IsException(pdu) ? ExceptionCodeOf(pdu) : null);
    }

    /// <summary>
    /// Startup sync of one push entry: read its remote range (FC01 for coil
    /// writes, FC03 for register writes) into the local device, then enable
    /// pushes for it. An exception response leaves it unsynced.
    /// </summary>
    internal async Task SyncFromRemoteAsync(NetworkStream stream, int index, CancellationToken ct)
    {
        var push = _pushes[index];
        int readFc = push.Fc is 5 or 15 ? 1 : 3;
        var req = ModbusClient.BuildReadRequest(readFc, push.RemoteOffset, push.Count);
        var pdu = await ExchangeAsync(stream, req, ct);
        if (pdu is null)
        {
            return;
        }
        if (!IsException(pdu))
        {
            Volatile.Write(ref _dirty[index], 0); // pre-sync local writes are discarded: the remote wins
            ApplyToLocal(readFc, push.Count, pdu, push.LocalTable, push.LocalOffset);
            Volatile.Write(ref _synced[index], 1);
        }
        _logger.Request(Name, _remoteUnit, (byte)readFc,
            RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
            IsException(pdu) ? ExceptionCodeOf(pdu) : null);
    }

    /// <summary>Write one push entry's current local cells to the remote.</summary>
    internal async Task PushAsync(NetworkStream stream, PushEntry push, CancellationToken ct)
    {
        byte[] req;
        if (push.Fc is 5 or 15)
        {
            var bits = new bool[push.Count];
            _local.Read(push.LocalTable, push.LocalOffset, bits);
            req = ModbusClient.BuildWriteRequest(push.Fc, push.RemoteOffset, bits);
        }
        else
        {
            var words = new ushort[push.Count];
            _local.Read(push.LocalTable, push.LocalOffset, words);
            req = ModbusClient.BuildWriteRequest(push.Fc, push.RemoteOffset, words);
        }
        var pdu = await ExchangeAsync(stream, req, ct);
        if (pdu is null)
        {
            return;
        }
        if (!IsException(pdu))
        {
            ModbusClient.CheckWriteResponse(push.Fc, push.RemoteOffset, push.Count, pdu);
        }
        _logger.Request(Name, _remoteUnit, (byte)push.Fc,
            RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
            IsException(pdu) ? ExceptionCodeOf(pdu) : null);
    }

    /// <summary>
    /// One MBAP request/response on the persistent connection. Returns the
    /// response PDU, or null when the unit id does not match (logged, nothing
    /// to apply). Throws IOException on timeout, malformed header or a
    /// transaction id mismatch.
    /// </summary>
    private async Task<byte[]?> ExchangeAsync(NetworkStream stream, byte[] req, CancellationToken ct)
    {
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
            await stream.ReadExactlyAsync(header, token);
            if (!Mbap.TryParseHeader(header, out var mbap))
            {
                throw new IOException("remote sent a malformed MBAP header");
            }
            if (mbap.TransactionId != tid)
            {
                throw new IOException($"response transaction id {mbap.TransactionId} ≠ request {tid}");
            }
            var pdu = new byte[mbap.Length - 1]; // TryParseHeader bounds the length
            await stream.ReadExactlyAsync(pdu, token);
            if (mbap.UnitId != _remoteUnit)
            {
                _logger.MalformedFrame(Name, _remote.ToString(), $"response unit id {mbap.UnitId} ≠ {_remoteUnit}");
                return null; // no answer to apply, stay on the connection
            }
            return pdu;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"request timed out after {_timeoutMs}ms");
        }
    }

    /// <summary>
    /// Parse a read response and mirror it into the local device (one Mirror ⇒
    /// one DeviceChange / variable log line). Mirror, not Write: the local
    /// table may be a read-only one (discrete/input).
    /// </summary>
    private void ApplyToLocal(int readFc, int count, ReadOnlySpan<byte> pdu, TableKind localTable, int localOffset)
    {
        // Watch callbacks run synchronously on this thread: mark it so our push
        // watchers can tell this write from a script/server write
        Volatile.Write(ref _selfWriteThread, Environment.CurrentManagedThreadId);
        try
        {
            if (readFc is 3 or 4)
            {
                var words = ModbusClient.ParseWordResponse(readFc, count, pdu);
                _local.Mirror(localTable, localOffset, words);
                return;
            }
            var bits = ModbusClient.ParseBitResponse(readFc, count, pdu);
            _local.Mirror(localTable, localOffset, bits);
        }
        finally
        {
            Volatile.Write(ref _selfWriteThread, -1);
        }
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
