using Microsoft.Extensions.Logging.Abstractions;
using Moz.Client;
using Moz.Devices;

namespace Moz.Tests;

/// <summary>
/// The protocol-agnostic client loop (<see cref="ClientPoller"/>) against a
/// fake <see cref="IRemoteDevice"/>: these pin the contract every protocol
/// implementation (Modbus, MC, ...) gets for free — interval polls, mirror
/// into any local table, push on local change only, pending/retry on
/// communication errors, no retry on a remote rejection, no echo of the
/// poller's own writes, lazy connect with per-cycle retry.
/// </summary>
public class ClientLoopTests
{
    [Fact]
    public async Task Poll_MirrorsBitsAndWords_IntoLocalTables_AndNotifies()
    {
        var remote = new FakeRemote
        {
            Polls = [new(TableKind.DiscreteInput, 5, 3), new(TableKind.InputRegister, 2, 2)],
            Bits = [true, false, true],
            Words = [0x1234, 0xABCD],
        };
        var local = new Device("local", discreteInputs: 16, inputRegisters: 16);
        int notified = 0;
        using var w = local.Watch(TableKind.InputRegister, 2, 2, _ => Interlocked.Increment(ref notified));
        var poller = new ClientPoller("p", remote, 50, local, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await task;

        var bits = new bool[3];
        local.Read(TableKind.DiscreteInput, 5, bits);
        Assert.Equal([true, false, true], bits);
        var words = new ushort[2];
        local.Read(TableKind.InputRegister, 2, words);
        Assert.Equal([0x1234, 0xABCD], words);
        Assert.True(remote.Reads >= 2);
        Assert.True(Volatile.Read(ref notified) >= 1);
        Assert.Equal(1, remote.Connects);   // one lazy connect, kept open
        Assert.True(remote.Closed);         // released at shutdown
    }

    [Fact]
    public async Task Poll_NullRead_IsRejection_NothingMirrored_LoopContinues()
    {
        var remote = new FakeRemote { Polls = [new(TableKind.HoldingRegister, 0, 1)], Words = null };
        var local = new Device("local", holdingRegisters: 8);
        local.Write(TableKind.HoldingRegister, 0, new ushort[] { 0x5555 });
        var poller = new ClientPoller("p", remote, 50, local, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await task;

        var words = new ushort[1];
        local.Read(TableKind.HoldingRegister, 0, words);
        Assert.Equal((ushort)0x5555, words[0]);
        Assert.True(remote.Reads >= 2);     // kept polling
        Assert.Equal(0, remote.Resets);     // a rejection is not a communication error
    }

    [Fact]
    public async Task Push_NothingAtStartup_LocalChange_WritesCurrentCells_Once()
    {
        var remote = new FakeRemote { Pushes = [new(TableKind.HoldingRegister, 4, 2), new(TableKind.Coil, 1, 1)] };
        var local = new Device("local", coils: 8, holdingRegisters: 8);
        local.Write(TableKind.HoldingRegister, 4, new ushort[] { 1, 2 }); // before the poller: not an event
        var poller = new ClientPoller("p", remote, 50, local, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(150);
        Assert.Empty(remote.Writes);
        Assert.False(poller.HasPendingPush);

        local.Write(TableKind.HoldingRegister, 4, new ushort[] { 7, 8 });
        local.WriteBit(TableKind.Coil, 1, true);
        await Task.Delay(150);
        await cts.CancelAsync();
        await task;

        Assert.Equal(2, remote.Writes.Count);
        Assert.Equal(0, remote.Writes[0].Item);
        Assert.Equal([7, 8], (ushort[])remote.Writes[0].Values);
        Assert.Equal(1, remote.Writes[1].Item);
        Assert.Equal([true], (bool[])remote.Writes[1].Values);
        Assert.False(poller.HasPendingPush);
    }

    [Fact]
    public async Task Push_WriteThrows_KeepsPending_ResetsLink_RetriesNextTick()
    {
        var remote = new FakeRemote { Pushes = [new(TableKind.HoldingRegister, 0, 1)], FailWrites = 2 };
        var local = new Device("local", holdingRegisters: 8);
        var provider = new CapturingProvider();
        var poller = new ClientPoller("p", remote, 50, local, provider.CreateLogger("p"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(80);
        local.Write(TableKind.HoldingRegister, 0, new ushort[] { 9 });
        await Task.Delay(60);
        Assert.True(poller.HasPendingPush);
        await Task.Delay(300);
        await cts.CancelAsync();
        await task;

        Assert.False(poller.HasPendingPush);
        Assert.Equal(2, remote.Resets);
        Assert.Single(remote.Writes);       // delivered exactly once after the failures
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id && e.Message.Contains("boom"));
    }

    [Fact]
    public async Task Push_PollWrites_DoNotEcho_ButScriptWritesDo()
    {
        var remote = new FakeRemote
        {
            Polls = [new(TableKind.HoldingRegister, 0, 2)],
            Pushes = [new(TableKind.HoldingRegister, 0, 2)],
            Words = [0x1111, 0x2222],
        };
        var local = new Device("local", holdingRegisters: 8);
        var poller = new ClientPoller("p", remote, 50, local, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(250);
        Assert.Empty(remote.Writes);        // many polls, no echo

        local.Write(TableKind.HoldingRegister, 0, new ushort[] { 0x3333, 0x4444 });
        await Task.Delay(150);
        await cts.CancelAsync();
        await task;
        Assert.Single(remote.Writes);
    }

    [Fact]
    public async Task Connect_Throws_LogsEachCycle_UntilItSucceeds()
    {
        var remote = new FakeRemote { Polls = [new(TableKind.HoldingRegister, 0, 1)], Words = [42], FailConnects = 2 };
        var local = new Device("local", holdingRegisters: 8);
        var provider = new CapturingProvider();
        var poller = new ClientPoller("p", remote, 50, local, provider.CreateLogger("p"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(300);
        Assert.True(poller.IsConnected);
        await cts.CancelAsync();
        await task;

        Assert.Equal(3, remote.Connects);
        Assert.Equal(2, provider.Entries.Count(e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id && e.Message.Contains("no link")));
        Assert.Equal(1, provider.Entries.Count(e => e.EventId == Moz.Logging.CommLog.EventIds.ConnAccepted.Id));
        var words = new ushort[1];
        local.Read(TableKind.HoldingRegister, 0, words);
        Assert.Equal((ushort)42, words[0]);
    }
}

/// <summary>In-memory remote: scripted answers, counters for every contract point.</summary>
internal sealed class FakeRemote : IRemoteDevice
{
    public IReadOnlyList<LocalRange> Polls { get; init; } = [];
    public IReadOnlyList<LocalRange> Pushes { get; init; } = [];

    /// <summary>Answer for bit polls; null = the remote rejects the request.</summary>
    public bool[]? Bits { get; init; }

    /// <summary>Answer for word polls; null = the remote rejects the request.</summary>
    public ushort[]? Words { get; init; }

    public int FailConnects { get; init; }
    public int FailWrites { get; init; }

    public int Connects;
    public int Reads;
    public int Resets;
    public bool Closed;
    public List<(int Item, Array Values)> Writes { get; } = [];

    public string Remote => "fake";
    public bool IsOpen { get; private set; }
    public bool IsConnected => IsOpen;

    public Task ConnectAsync(CancellationToken ct)
    {
        Connects++;
        if (Connects <= FailConnects)
        {
            throw new IOException("no link");
        }
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task<bool[]?> ReadBitsAsync(int poll, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(Bits?.ToArray());
    }

    public Task<ushort[]?> ReadWordsAsync(int poll, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(Words?.ToArray());
    }

    public Task WriteBitsAsync(int push, bool[] values, CancellationToken ct) => Write(push, values);

    public Task WriteWordsAsync(int push, ushort[] values, CancellationToken ct) => Write(push, values);

    private Task Write(int push, Array values)
    {
        if (Writes.Count + Resets < FailWrites)
        {
            throw new IOException("boom");
        }
        Writes.Add((push, values));
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct)
    {
        Resets++;
        IsOpen = false;
        return Task.CompletedTask;
    }

    public void Close() => Closed = true;
}
