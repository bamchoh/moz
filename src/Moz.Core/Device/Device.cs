namespace Moz.Devices;

/// <summary>
/// The concrete in-memory device. One lock guards all four tables; watch
/// callbacks are dispatched outside the lock so callbacks may re-enter.
/// </summary>
public sealed class Device : IDeviceAccess
{
    private readonly Lock _sync = new();
    private readonly bool[] _bits;
    private readonly bool[] _readOnlyBits;
    private readonly ushort[] _words;
    private readonly ushort[] _readOnlyWords;
    private readonly List<Watcher> _watchers = [];

    public Device(string name, int bits = 65536, int readOnlyBits = 65536,
                  int words = 65536, int readOnlyWords = 65536)
    {
        Name = name;
        _bits = new bool[bits];
        _readOnlyBits = new bool[readOnlyBits];
        _words = new ushort[words];
        _readOnlyWords = new ushort[readOnlyWords];
    }

    public string Name { get; }

    public int Length(TableKind table) =>
        table switch
        {
            TableKind.Bits => _bits.Length,
            TableKind.ReadOnlyBits => _readOnlyBits.Length,
            TableKind.Words => _words.Length,
            TableKind.ReadOnlyWords => _readOnlyWords.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };

    public bool ReadBit(TableKind table, int index)
    {
        CheckBounds(table, index, 1);
        lock (_sync)
        {
            return Bits(table)[index];
        }
    }

    public void WriteBit(TableKind table, int index, bool value)
    {
        EnsureWritableBit(table);
        Span<bool> one = [value];
        var change = ApplyWrite(table, index, one);
        Dispatch(change);
    }

    public void Read(TableKind table, int index, Span<bool> dest)
    {
        CheckBounds(table, index, dest.Length);
        lock (_sync)
        {
            Bits(table).AsSpan(index, dest.Length).CopyTo(dest);
        }
    }

    public void Read(TableKind table, int index, Span<ushort> dest)
    {
        CheckBounds(table, index, dest.Length);
        lock (_sync)
        {
            Words(table).AsSpan(index, dest.Length).CopyTo(dest);
        }
    }

    public void Write(TableKind table, int index, ReadOnlySpan<bool> src)
    {
        EnsureWritableBit(table);
        var change = ApplyWrite(table, index, src);
        Dispatch(change);
    }

    public void Write(TableKind table, int index, ReadOnlySpan<ushort> src)
    {
        EnsureWritableWord(table);
        var change = ApplyWrite(table, index, src);
        Dispatch(change);
    }

    public IDisposable Watch(TableKind table, int index, int count, Action<DeviceChange> callback)
    {
        CheckBounds(table, index, count);
        ArgumentNullException.ThrowIfNull(callback);
        var watcher = new Watcher(table, index, count, callback);
        lock (_sync)
        {
            _watchers.Add(watcher);
        }
        return new Unsubscriber(this, watcher);
    }

    /// <summary>
    /// Seed read-only tables (ReadOnlyBits/ReadOnlyWords) and preset any
    /// table from the owning process: config seeding today, tests, and a
    /// future CLI/GUI "set value" feature. Intentionally NOT part of
    /// <see cref="IDeviceAccess"/> — external consumers must use the seam.
    /// </summary>
    internal void Seed(TableKind table, int index, ReadOnlySpan<bool> src)
    {
        CheckBounds(table, index, src.Length);
        lock (_sync)
        {
            src.CopyTo(Bits(table).AsSpan(index, src.Length));
        }
    }

    internal void Seed(TableKind table, int index, ReadOnlySpan<ushort> src)
    {
        CheckBounds(table, index, src.Length);
        lock (_sync)
        {
            src.CopyTo(Words(table).AsSpan(index, src.Length));
        }
    }

    /// <summary>
    /// Owner-side write into ANY table, including the read-only ones, with
    /// watcher notification: the client mirror lands remote read_only_bits/read_only_words
    /// values here. Like <see cref="Seed"/> it bypasses the read-only rule
    /// (which exists for Modbus masters, not for the process that owns the
    /// simulated memory); unlike Seed it dispatches a DeviceChange.
    /// </summary>
    internal void Mirror(TableKind table, int index, ReadOnlySpan<bool> src) =>
        Dispatch(ApplyWrite(table, index, src));

    internal void Mirror(TableKind table, int index, ReadOnlySpan<ushort> src) =>
        Dispatch(ApplyWrite(table, index, src));

    // --- internals -------------------------------------------------------

    private bool[] Bits(TableKind table) =>
        table switch
        {
            TableKind.Bits => _bits,
            TableKind.ReadOnlyBits => _readOnlyBits,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "not a bit table"),
        };

    private ushort[] Words(TableKind table) =>
        table switch
        {
            TableKind.Words => _words,
            TableKind.ReadOnlyWords => _readOnlyWords,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "not a word table"),
        };

    private void CheckBounds(TableKind table, int index, int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must be >= 0");
        }
        if (index < 0 || index + count > Length(table))
        {
            throw new IndexOutOfRangeException(
                $"range [{index}, {index + count}) exceeds {table} length {Length(table)}");
        }
    }

    private void EnsureWritableBit(TableKind table)
    {
        if (table == TableKind.Bits)
        {
            return;
        }
        ThrowNotWritable(table, TableKind.Bits);
    }

    private void EnsureWritableWord(TableKind table)
    {
        if (table == TableKind.Words)
        {
            return;
        }
        ThrowNotWritable(table, TableKind.Words);
    }

    private static void ThrowNotWritable(TableKind table, TableKind writableKind)
    {
        if (table is TableKind.ReadOnlyBits or TableKind.ReadOnlyWords)
        {
            throw new InvalidOperationException(
                $"{table} is read-only. This is a programming bug: the Modbus PDU layer must map this to exception 02 before reaching the device.");
        }
        if (table is TableKind.Bits or TableKind.Words)
        {
            throw new InvalidOperationException(
                $"table kind mismatch: {table} requires the {(table == TableKind.Bits ? "bit" : "word")} Write; expected writes go to {writableKind}");
        }
        throw new ArgumentOutOfRangeException(nameof(table), table, null);
    }

    private DeviceChange ApplyWrite(TableKind table, int index, ReadOnlySpan<bool> src)
    {
        CheckBounds(table, index, src.Length);
        var bits = Bits(table);
        lock (_sync)
        {
            src.CopyTo(bits.AsSpan(index, src.Length));
        }
        return new DeviceChange(table, index, src.Length);
    }

    private DeviceChange ApplyWrite(TableKind table, int index, ReadOnlySpan<ushort> src)
    {
        CheckBounds(table, index, src.Length);
        var words = Words(table);
        lock (_sync)
        {
            src.CopyTo(words.AsSpan(index, src.Length));
        }
        return new DeviceChange(table, index, src.Length);
    }

    /// <summary>
    /// Snapshot watchers overlapping the change, then invoke outside the lock
    /// (re-entrancy is normal: callbacks may write back to the device).
    /// </summary>
    private void Dispatch(DeviceChange change)
    {
        if (change.Count == 0)
        {
            return;
        }
        Watcher[] snapshot;
        lock (_sync)
        {
            if (_watchers.Count == 0)
            {
                return; // common fast path: nobody watches
            }
            snapshot = _watchers.Where(w => w.Overlaps(change)).ToArray();
        }
        foreach (var w in snapshot)
        {
            w.Callback(change);
        }
    }

    private void Unsubscribe(Watcher watcher)
    {
        lock (_sync)
        {
            _watchers.Remove(watcher);
        }
    }

    private sealed class Watcher(TableKind table, int index, int count, Action<DeviceChange> callback)
    {
        private readonly int _end = index + count;

        public Action<DeviceChange> Callback { get; } = callback;

        /// <summary>[index, index+count) of the watcher intersects [Start, Start+Count) of the change.</summary>
        public bool Overlaps(DeviceChange change) =>
            change.Table == table &&
            change.Start < _end &&
            index < change.Start + change.Count;
    }

    private sealed class Unsubscriber(Device owner, Watcher watcher) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(watcher);
            }
        }
    }
}