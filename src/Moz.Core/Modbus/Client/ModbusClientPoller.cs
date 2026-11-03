using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Modbus.Client;

/// <summary>One poll line: read [remote_offset, +count) with fc, write into local table at local_offset.</summary>
public readonly record struct PollEntry(int Fc, int RemoteOffset, int Count, TableKind LocalTable, int LocalOffset);

/// <summary>One push line: write local [local_offset, +count) to the remote at remote_offset with fc (5/6/15/16).</summary>
public readonly record struct PushEntry(int Fc, TableKind LocalTable, int LocalOffset, int Count, int RemoteOffset);

/// <summary>
/// moz as Modbus master: talks to a remote slave over an <see cref="IMasterLink"/>
/// (TCP or RTU), mirrors reads into a local device on a fixed interval
/// (polls) and writes local changes back to the remote (pushes). One link per
/// client; every error (connect, timeout, exception response) is logged and
/// the loop retries next tick — never fatal.
///
/// Push policy: a push reacts to local WRITES only. Nothing is sent at
/// startup and the remote is never read for a push range (declare a poll
/// for that). Every local change (<see cref="IDeviceAccess.Watch"/> marks
/// the entry dirty and wakes the loop) is written to the remote as soon as
/// the link is available; a change made while the remote is unreachable
/// stays dirty and is delivered on the next successful exchange. Changes the
/// poller itself makes while applying a poll never trigger a push, so the
/// same cells may be polled and pushed (bidirectional mirror) without an echo
/// loop. A communication error keeps the entry dirty (retried next tick);
/// an exception response from the remote is logged and the entry is
/// considered delivered.
/// </summary>
public sealed class ModbusClientPoller : IProtocolRunner
{
    private readonly IMasterLink _link;
    private readonly byte _remoteUnit;
    private readonly int _intervalMs;
    private readonly Device _local;
    private readonly PollEntry[] _polls;
    private readonly PushEntry[] _pushes;
    private readonly int[] _dirty;                        // per push entry: 1 = local changed since last delivery
    private readonly SemaphoreSlim _wake = new(0, 1);     // released by a local change to cut the interval wait short
    private readonly ILogger _logger;

    private int _selfWriteThread = -1;                    // thread applying a poll to the local device (its Watch callbacks are ours)

    public ModbusClientPoller(string name, IMasterLink link, byte remoteUnit, int intervalMs,
        Device local, PollEntry[] polls, ILogger logger, PushEntry[]? pushes = null)
    {
        Name = name;
        _link = link;
        _remoteUnit = remoteUnit;
        _intervalMs = intervalMs;
        _local = local;
        _polls = polls;
        _pushes = pushes ?? [];
        _dirty = new int[_pushes.Length];
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>The remote is reachable (see <see cref="IMasterLink.IsConnected"/>); feeds plc.getVariableStatus.</summary>
    public bool IsConnected => _link.IsConnected;

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
            _link.Close();
        }
    }

    /// <summary>Watch callback: ignore our own poll writes, otherwise mark dirty and wake the loop.</summary>
    private void OnLocalChange(int index)
    {
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _selfWriteThread))
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

    /// <summary>One cycle: open the link lazily, deliver dirty pushes, then (when due) run all polls.</summary>
    private async Task RunCycleAsync(bool pollDue, CancellationToken ct)
    {
        if (!await OpenLinkAsync(ct))
        {
            return; // failure already logged
        }
        for (int i = 0; i < _pushes.Length; i++)
        {
            if (Volatile.Read(ref _dirty[i]) == 0)
            {
                continue;
            }
            Volatile.Write(ref _dirty[i], 0); // cleared before reading the local values: a later change re-dirties
            try
            {
                await PushAsync(_pushes[i], ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _dirty[i], 1); // undelivered: retry next tick
                _logger.ConnError(Name, _link.Remote, ex.Message);
                await _link.ResetAsync(ct);
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
                await MirrorPollAsync(poll, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // any bad response poisons the exchange state: reset the link, retry next tick
                _logger.ConnError(Name, _link.Remote, ex.Message);
                await _link.ResetAsync(ct);
                return;
            }
        }
    }

    /// <summary>Lazy link establishment; a failure is logged once per cycle.</summary>
    private async Task<bool> OpenLinkAsync(CancellationToken ct)
    {
        if (_link.IsOpen)
        {
            return true;
        }
        try
        {
            await _link.ConnectAsync(ct);
            _logger.ConnAccepted(Name, _link.Remote);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.ConnError(Name, _link.Remote, ex.Message);
            return false;
        }
    }

    /// <summary>Read one poll entry from the remote and write it into the local device.</summary>
    private async Task MirrorPollAsync(PollEntry poll, CancellationToken ct)
    {
        var req = ModbusClient.BuildReadRequest(poll.Fc, poll.RemoteOffset, poll.Count);
        var pdu = await _link.ExchangeAsync(_remoteUnit, req, ct);
        if (!IsException(pdu))
        {
            ApplyToLocal(poll.Fc, poll.Count, pdu, poll.LocalTable, poll.LocalOffset);
        }
        _logger.Request(Name, _remoteUnit, (byte)poll.Fc,
            RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
            IsException(pdu) ? ExceptionCodeOf(pdu) : null);
    }

    /// <summary>Write one push entry's current local cells to the remote.</summary>
    private async Task PushAsync(PushEntry push, CancellationToken ct)
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
        var pdu = await _link.ExchangeAsync(_remoteUnit, req, ct);
        if (!IsException(pdu))
        {
            ModbusClient.CheckWriteResponse(push.Fc, push.RemoteOffset, push.Count, pdu);
        }
        _logger.Request(Name, _remoteUnit, (byte)push.Fc,
            RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
            IsException(pdu) ? ExceptionCodeOf(pdu) : null);
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
}
