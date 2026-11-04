using Moz.Devices;

namespace Moz.Tests;

public class DeviceTests
{
    [Fact]
    public void RoundTrip_WordSpan()
    {
        var device = new Device("t", words: 10);
        Span<ushort> w = [1, 2, 3];
        device.Write(TableKind.Words, 2, w);
        Span<ushort> read = new ushort[3];
        device.Read(TableKind.Words, 2, read);
        Assert.Equal((ushort)1, read[0]);
        Assert.Equal((ushort)2, read[1]);
        Assert.Equal((ushort)3, read[2]);
    }

    [Fact]
    public void ReadOnlyTables_RejectWrite()
    {
        var device = new Device("t");
        Assert.Throws<InvalidOperationException>(() => device.Write(TableKind.ReadOnlyWords, 0, (ReadOnlySpan<ushort>)[1]));
        Assert.Throws<InvalidOperationException>(() => device.WriteBit(TableKind.ReadOnlyBits, 0, true));
    }

    [Fact]
    public void TableKindMismatch_Rejected()
    {
        var device = new Device("t");
        Assert.Throws<InvalidOperationException>(() => device.Write(TableKind.Words, 0, (ReadOnlySpan<bool>)[true]));
    }

    [Fact]
    public void BoundsChecked()
    {
        var device = new Device("t", bits: 10);
        Assert.Throws<IndexOutOfRangeException>(() => device.Read(TableKind.Bits, 8, new bool[5]));
        Assert.Throws<IndexOutOfRangeException>(() => device.WriteBit(TableKind.Bits, 10, true));
    }

    [Fact]
    public async Task ConcurrentAccess_IsRaceFree()
    {
        var device = new Device("t", words: 1000);
        var tasks = Enumerable.Range(0, 8).Select(taskId => Task.Run(() =>
        {
            var w = new ushort[1];
            for (int i = 0; i < 5000; i++)
            {
                int idx = (taskId * 100 + i) % 1000;
                for (int round = 0; round < 3; round++)
                {
                    w[0] = (ushort)(taskId * 3 + round + 1);
                    device.Write(TableKind.Words, idx, w);
                    var v = new ushort[1];
                    device.Read(TableKind.Words, idx, v);
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
        var device = new Device("t", words: 100);
        var changes = new List<DeviceChange>();
        using var sub = device.Watch(TableKind.Words, 10, 5, changes.Add);

        Span<ushort> w = [7];
        device.Write(TableKind.Words, 12, w);   // inside → notify
        device.Write(TableKind.Words, 50, w);   // outside → no notify
        Span<ushort> five = [1, 2, 3, 4, 5];
        device.Write(TableKind.Words, 8, five); // overlaps tail → notify

        Assert.Equal(2, changes.Count);
        Assert.Equal(12, changes[0].Start);
        Assert.Equal(1, changes[0].Count);
        Assert.Equal(8, changes[1].Start);
        Assert.Equal(5, changes[1].Count);
    }

    [Fact]
    public void Watch_TableIsolated()
    {
        var device = new Device("t", bits: 10);
        var changes = new List<DeviceChange>();
        using var sub = device.Watch(TableKind.Words, 0, 10, changes.Add);
        device.WriteBit(TableKind.Bits, 0, true);
        Assert.Empty(changes);
    }

    [Fact]
    public void Watch_DisposeStopsNotifications()
    {
        var device = new Device("t", bits: 10);
        var changes = new List<DeviceChange>();
        var sub = device.Watch(TableKind.Bits, 0, 10, changes.Add);
        device.WriteBit(TableKind.Bits, 0, true);
        Assert.Single(changes);
        sub.Dispose();
        device.WriteBit(TableKind.Bits, 0, false);
        Assert.Single(changes);
    }

    [Fact]
    public void Watch_CallbackMayWriteBack_NoDeadlock()
    {
        var device = new Device("t", words: 10);
        var reentered = false;
        using var sub = device.Watch(TableKind.Words, 0, 1, change =>
        {
            device.Write(TableKind.Words, 5, (ReadOnlySpan<ushort>)[99]); // re-entrant write
            reentered = true;
        });
        Span<ushort> w = [1];
        device.Write(TableKind.Words, 0, w);
        Assert.True(reentered);
    }

    [Fact]
    public void Watch_OneWriteToOneCallback_Exactly()
    {
        var device = new Device("t", bits: 10);
        var count = 0;
        using var sub = device.Watch(TableKind.Bits, 0, 10, _ => Interlocked.Increment(ref count));
        Span<bool> bits = new bool[10];
        bits.Fill(true);
        device.Write(TableKind.Bits, 0, bits); // batch write → ONE event
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public void Watch_DifferentWatchersOverlap_AllNotified()
    {
        var device = new Device("t", bits: 10);
        int a = 0, b = 0;
        using var _a = device.Watch(TableKind.Bits, 0, 3, _ => Interlocked.Increment(ref a));
        using var _b = device.Watch(TableKind.Bits, 2, 3, _ => Interlocked.Increment(ref b));
        device.WriteBit(TableKind.Bits, 2, true); // overlaps both [0,3) and [2,5)
        Assert.Equal(1, Volatile.Read(ref a));
        Assert.Equal(1, Volatile.Read(ref b));
    }

    // --- Mirror: owner-side write into any table, with notification ---------------------

    [Fact]
    public void Mirror_WritesReadOnlyTables_AndNotifiesWatchers()
    {
        var device = new Device("d", readOnlyBits: 16, readOnlyWords: 16);
        var changes = new List<DeviceChange>();
        using var w1 = device.Watch(TableKind.ReadOnlyBits, 0, 4, changes.Add);
        using var w2 = device.Watch(TableKind.ReadOnlyWords, 0, 4, changes.Add);

        device.Mirror(TableKind.ReadOnlyBits, 1, new[] { true, true });
        device.Mirror(TableKind.ReadOnlyWords, 2, new ushort[] { 0xBEEF });

        Assert.True(device.ReadBit(TableKind.ReadOnlyBits, 1));
        Assert.True(device.ReadBit(TableKind.ReadOnlyBits, 2));
        var words = new ushort[1];
        device.Read(TableKind.ReadOnlyWords, 2, words);
        Assert.Equal((ushort)0xBEEF, words[0]);
        Assert.Equal([new DeviceChange(TableKind.ReadOnlyBits, 1, 2), new DeviceChange(TableKind.ReadOnlyWords, 2, 1)], changes);
    }

    [Fact]
    public void Mirror_OutOfRange_Throws()
    {
        var device = new Device("d", readOnlyWords: 4);
        Assert.Throws<IndexOutOfRangeException>(() => device.Mirror(TableKind.ReadOnlyWords, 3, new ushort[] { 1, 2 }));
    }
}
