using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Client;

/// <summary>
/// moz as a client (master) of one remote device, over any protocol that
/// implements <see cref="IRemoteDevice"/>: mirrors reads into a local device
/// on a fixed interval (polls) and writes local changes back to the remote
/// (pushes). Every error (connect, timeout, bad response) is logged and the
/// loop retries next tick — never fatal.
///
/// Push policy: a push reacts to local WRITES only. Nothing is sent at
/// startup and the remote is never read for a push range (declare a poll
/// for that). Every local change (<see cref="IDeviceAccess.Watch"/> marks
/// the item dirty and wakes the loop) is written to the remote as soon as
/// the link is available; a change made while the remote is unreachable
/// stays dirty and is delivered on the next successful write. Changes the
/// poller itself makes while applying a poll never trigger a push, so the
/// same cells may be polled and pushed (bidirectional mirror) without an echo
/// loop. A communication error keeps the item dirty (retried next tick); a
/// rejection by the remote is the implementation's to log and the item is
/// considered delivered.
/// </summary>
public sealed class ClientPoller : IProtocolRunner
{
    private readonly IRemoteDevice _remote;
    private readonly int _intervalMs;
    private readonly Device _local;
    private readonly int[] _dirty;                        // per push item: 1 = local changed since last delivery
    private readonly SemaphoreSlim _wake = new(0, 1);     // released by a local change to cut the interval wait short
    private readonly ILogger _logger;

    private int _selfWriteThread = -1;                    // thread applying a poll to the local device (its Watch callbacks are ours)

    public ClientPoller(string name, IRemoteDevice remote, int intervalMs, Device local, ILogger logger)
    {
        Name = name;
        _remote = remote;
        _intervalMs = intervalMs;
        _local = local;
        _dirty = new int[remote.Pushes.Count];
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>The protocol implementation (its poll/push ranges feed plc.getVariableStatus).</summary>
    public IRemoteDevice Remote => _remote;

    /// <summary>The remote is reachable (see <see cref="IRemoteDevice.IsConnected"/>).</summary>
    public bool IsConnected => _remote.IsConnected;

    /// <summary>True while at least one push item has an undelivered local change.</summary>
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
        var watches = new List<IDisposable>(_dirty.Length);
        try
        {
            for (int i = 0; i < _dirty.Length; i++)
            {
                int index = i;
                var push = _remote.Pushes[i];
                watches.Add(_local.Watch(push.Table, push.Offset, push.Count, _ => OnLocalChange(index)));
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
            _remote.Close();
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
        for (int i = 0; i < _dirty.Length; i++)
        {
            if (Volatile.Read(ref _dirty[i]) == 0)
            {
                continue;
            }
            Volatile.Write(ref _dirty[i], 0); // cleared before reading the local values: a later change re-dirties
            try
            {
                await PushAsync(i, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _dirty[i], 1); // undelivered: retry next tick
                _logger.ConnError(Name, _remote.Remote, ex.Message);
                await _remote.ResetAsync(ct);
                return;
            }
        }
        if (!pollDue)
        {
            return;
        }
        for (int i = 0; i < _remote.Polls.Count; i++)
        {
            try
            {
                await PollAsync(i, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // any bad response poisons the exchange state: reset the link, retry next tick
                _logger.ConnError(Name, _remote.Remote, ex.Message);
                await _remote.ResetAsync(ct);
                return;
            }
        }
    }

    /// <summary>Lazy link establishment; a failure is logged once per cycle.</summary>
    private async Task<bool> OpenLinkAsync(CancellationToken ct)
    {
        if (_remote.IsOpen)
        {
            return true;
        }
        try
        {
            await _remote.ConnectAsync(ct);
            _logger.ConnAccepted(Name, _remote.Remote);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.ConnError(Name, _remote.Remote, ex.Message);
            return false;
        }
    }

    /// <summary>Read one poll item from the remote and mirror it into the local device.</summary>
    private async Task PollAsync(int item, CancellationToken ct)
    {
        var range = _remote.Polls[item];
        if (range.IsBit)
        {
            var bits = await _remote.ReadBitsAsync(item, ct);
            if (bits is not null)
            {
                Mirror(range, bits);
            }
            return;
        }
        var words = await _remote.ReadWordsAsync(item, ct);
        if (words is not null)
        {
            Mirror(range, words);
        }
    }

    /// <summary>Write one push item's current local cells to the remote.</summary>
    private Task PushAsync(int item, CancellationToken ct)
    {
        var range = _remote.Pushes[item];
        if (range.IsBit)
        {
            var bits = new bool[range.Count];
            _local.Read(range.Table, range.Offset, bits);
            return _remote.WriteBitsAsync(item, bits, ct);
        }
        var words = new ushort[range.Count];
        _local.Read(range.Table, range.Offset, words);
        return _remote.WriteWordsAsync(item, words, ct);
    }

    /// <summary>
    /// Mirror a poll result into the local device (one Mirror ⇒ one
    /// DeviceChange / variable log line). Mirror, not Write: the local table
    /// may be a read-only one (discrete/input). Watch callbacks run
    /// synchronously on this thread: it is marked so the push watchers can
    /// tell this write from a script/server write.
    /// </summary>
    private void Mirror(LocalRange range, bool[] bits)
    {
        Volatile.Write(ref _selfWriteThread, Environment.CurrentManagedThreadId);
        try
        {
            _local.Mirror(range.Table, range.Offset, bits);
        }
        finally
        {
            Volatile.Write(ref _selfWriteThread, -1);
        }
    }

    private void Mirror(LocalRange range, ushort[] words)
    {
        Volatile.Write(ref _selfWriteThread, Environment.CurrentManagedThreadId);
        try
        {
            _local.Mirror(range.Table, range.Offset, words);
        }
        finally
        {
            Volatile.Write(ref _selfWriteThread, -1);
        }
    }
}
