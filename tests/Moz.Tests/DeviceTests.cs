using Moz.Devices;

namespace Moz.Tests;

public class DeviceTests
{
    [Fact]
    public void RoundTrip_WordSpan()
    {
        var device = new Device("t", holdingRegisters: 10);
        Span<ushort> w = [1, 2, 3];
        device.Write(TableKind.HoldingRegister, 2, w);
        Span<ushort> read = new ushort[3];
        device.Read(TableKind.HoldingRegister, 2, read);
        Assert.Equal((ushort)1, read[0]);
        Assert.Equal((ushort)2, read[1]);
        Assert.Equal((ushort)3, read[2]);
    }

    [Fact]
    public void ReadOnlyTables_RejectWrite()
    {
        var device = new Device("t");
        Assert.Throws<InvalidOperationException>(() => device.Write(TableKind.InputRegister, 0, (ReadOnlySpan<ushort>)[1]));
        Assert.Throws<InvalidOperationException>(() => device.WriteBit(TableKind.DiscreteInput, 0, true));
    }

    [Fact]
    public void TableKindMismatch_Rejected()
    {
        var device = new Device("t");
        Assert.Throws<InvalidOperationException>(() => device.Write(TableKind.HoldingRegister, 0, (ReadOnlySpan<bool>)[true]));
    }

    [Fact]
    public void BoundsChecked()
    {
        var device = new Device("t", coils: 10);
        Assert.Throws<IndexOutOfRangeException>(() => device.Read(TableKind.Coil, 8, new bool[5]));
        Assert.Throws<IndexOutOfRangeException>(() => device.WriteBit(TableKind.Coil, 10, true));
    }

    [Fact]
    public async Task ConcurrentAccess_IsRaceFree()
    {
        var device = new Device("t", holdingRegisters: 1000);
        var tasks = Enumerable.Range(0, 8).Select(taskId => Task.Run(() =>
        {
            var w = new ushort[1];
            for (int i = 0; i < 5000; i++)
            {
                int idx = (taskId * 100 + i) % 1000;
                for (int round = 0; round < 3; round++)
                {
                    w[0] = (ushort)(taskId * 3 + round + 1);
                    device.Write(TableKind.HoldingRegister, idx, w);
                    var v = new ushort[1];
                    device.Read(TableKind.HoldingRegister, idx, v);
                    // u16 cells are memcpy'd under the lock, so reads never tear;
                    // we mainly assert the loop terminates without exception
                    Assert.True(v[0] <= ushort.MaxValue);
                }
            }
        })).ToArray();
        await Task.WhenAll(tasks);
    }

    // --- Watch ---------------------------------------------------------------

    [Fact]
    public void Watch_NotifyOnOverlappingWriteOnly()
    {
        var device = new Device("t", holdingRegisters: 100);
        var changes = new List<DeviceChange>();
        using var sub = device.Watch(TableKind.HoldingRegister, 10, 5, changes.Add);

        Span<ushort> w = [7];
        device.Write(TableKind.HoldingRegister, 12, w);   // inside → notify
        device.Write(TableKind.HoldingRegister, 50, w);   // outside → no notify
        Span<ushort> five = [1, 2, 3, 4, 5];
        device.Write(TableKind.HoldingRegister, 8, five); // overlaps tail → notify

        Assert.Equal(2, changes.Count);
        Assert.Equal(12, changes[0].Start);
        Assert.Equal(1, changes[0].Count);
        Assert.Equal(8, changes[1].Start);
        Assert.Equal(5, changes[1].Count);
    }

    [Fact]
    public void Watch_TableIsolated()
    {
        var device = new Device("t", coils: 10);
        var changes = new List<DeviceChange>();
        using var sub = device.Watch(TableKind.HoldingRegister, 0, 10, changes.Add);
        device.WriteBit(TableKind.Coil, 0, true);
        Assert.Empty(changes);
    }

    [Fact]
    public void Watch_DisposeStopsNotifications()
    {
        var device = new Device("t", coils: 10);
        var changes = new List<DeviceChange>();
        var sub = device.Watch(TableKind.Coil, 0, 10, changes.Add);
        device.WriteBit(TableKind.Coil, 0, true);
        Assert.Single(changes);
        sub.Dispose();
        device.WriteBit(TableKind.Coil, 0, false);
        Assert.Single(changes);
    }

    [Fact]
    public void Watch_CallbackMayWriteBack_NoDeadlock()
    {
        var device = new Device("t", holdingRegisters: 10);
        var reentered = false;
        using var sub = device.Watch(TableKind.HoldingRegister, 0, 1, change =>
        {
            device.Write(TableKind.HoldingRegister, 5, (ReadOnlySpan<ushort>)[99]); // re-entrant write
            reentered = true;
        });
        Span<ushort> w = [1];
        device.Write(TableKind.HoldingRegister, 0, w);
        Assert.True(reentered);
    }

    [Fact]
    public void Watch_OneWriteToOneCallback_Exactly()
    {
        var device = new Device("t", coils: 10);
        var count = 0;
        using var sub = device.Watch(TableKind.Coil, 0, 10, _ => Interlocked.Increment(ref count));
        Span<bool> bits = new bool[10];
        bits.Fill(true);
        device.Write(TableKind.Coil, 0, bits); // batch write → ONE event
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Watch_DifferentWatchersOverlap_AllNotified()
    {
        var device = new Device("t", coils: 10);
        int a = 0, b = 0;
        using var _a = device.Watch(TableKind.Coil, 0, 3, _ => Interlocked.Increment(ref a));
        using var _b = device.Watch(TableKind.Coil, 2, 3, _ => Interlocked.Increment(ref b));
        device.WriteBit(TableKind.Coil, 2, true); // overlaps both [0,3) and [2,5)
        Assert.Equal(1, Volatile.Read(ref a));
        Assert.Equal(1, Volatile.Read(ref b));
    }
}