namespace Moz.Devices;

/// <summary>
/// Descriptor of one batch mutation. Watchers re-read the affected range
/// with <see cref="IDeviceAccess.Read"/>; the event itself carries no values.
/// </summary>
/// <param name="Table">Table that was mutated.</param>
/// <param name="Start">0-based index of the first affected cell.</param>
/// <param name="Count">Number of affected cells.</param>
public readonly record struct DeviceChange(TableKind Table, int Start, int Count);

/// <summary>
/// THE seam of moz. Everything that touches simulated memory goes through
/// this surface: the Modbus transports today; typed variables (later phase),
/// the OPC UA server and the GUI consume it unchanged.
/// All buffers are caller-owned; implementations never retain them.
/// </summary>
public interface IDeviceAccess
{
    /// <summary>Number of cells (bits or words) in <paramref name="table"/>.</summary>
    int Length(TableKind table);

    /// <summary>Read one bit cell.</summary>
    bool ReadBit(TableKind table, int index);

    /// <summary>
    /// Write one bit cell. Writing a read-only (DiscreteInput/InputRegister)
    /// table throws <see cref="InvalidOperationException"/> — that is a
    /// programming bug, and the PDU layer rejects it earlier (exception 02).
    /// </summary>
    void WriteBit(TableKind table, int index, bool value);

    /// <summary>Read a run of bit cells from a bit table.</summary>
    void Read(TableKind table, int index, Span<bool> dest);

    /// <summary>Read a run of word cells from a word table.</summary>
    void Read(TableKind table, int index, Span<ushort> dest);

    /// <summary>Write a run of bit cells to a bit table (bit tables only).</summary>
    void Write(TableKind table, int index, ReadOnlySpan<bool> src);

    /// <summary>Write a run of word cells to a word table (word tables only).</summary>
    void Write(TableKind table, int index, ReadOnlySpan<ushort> src);

    /// <summary>
    /// Subscribe to mutations overlapping [index, index+count).
    /// Callbacks run AFTER the mutation completes, outside the device lock,
    /// so a callback may itself call <see cref="IDeviceAccess.Write"/>.
    /// One mutating call produces exactly one callback. Dispose unsubscribes.
    /// </summary>
    IDisposable Watch(TableKind table, int index, int count, Action<DeviceChange> callback);
}