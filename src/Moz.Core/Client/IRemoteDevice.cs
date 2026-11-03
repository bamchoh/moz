using Moz.Devices;

namespace Moz.Client;

/// <summary>A range of local cells mirrored by one poll or push item.</summary>
public readonly record struct LocalRange(TableKind Table, int Offset, int Count)
{
    /// <summary>Bit tables (coil/discrete) carry bools, word tables carry ushorts.</summary>
    public bool IsBit => Table is TableKind.Coil or TableKind.DiscreteInput;
}

/// <summary>
/// One remote device reached through one protocol — the only thing a client
/// protocol has to implement. The generic loop (<see cref="ClientPoller"/>)
/// owns timing, local watches, the pending/retry policy, mirroring into the
/// local device and the connection log lines; the implementation owns the
/// wire format, the remote addressing and the per-request log line.
///
/// Items: <see cref="Polls"/>[i] / <see cref="Pushes"/>[i] describe the local
/// side of item i; what the item reads or writes on the remote is the
/// implementation's business (a Modbus fc + offset, an MC device like D100, a
/// node id, ...). Cells are the common currency: bits for bit tables, words
/// for word tables.
///
/// Errors: throw (any exception; usually <see cref="IOException"/>) for a
/// communication failure — the loop logs it, calls <see cref="ResetAsync"/>,
/// keeps a push pending and retries next tick. A request the remote answered
/// with a rejection (Modbus exception, MC end code, ...) is NOT an error:
/// log it, return null from a read (nothing to mirror) or return normally
/// from a write (delivered, not retried).
/// </summary>
public interface IRemoteDevice
{
    /// <summary>Remote description for log lines ("127.0.0.1:502", "COM10").</summary>
    string Remote { get; }

    /// <summary>The link is established (socket connected / port open); the loop connects lazily when false.</summary>
    bool IsOpen { get; }

    /// <summary>The remote is reachable (feeds plc.getVariableStatus); may be false while open, e.g. a silent serial slave.</summary>
    bool IsConnected { get; }

    /// <summary>Local ranges of the poll items (remote → local), in item order.</summary>
    IReadOnlyList<LocalRange> Polls { get; }

    /// <summary>Local ranges of the push items (local → remote), in item order.</summary>
    IReadOnlyList<LocalRange> Pushes { get; }

    /// <summary>Establish the link. Throws on failure (logged, retried next cycle).</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Read poll item <paramref name="poll"/> (a bit range): Polls[poll].Count bools, or null when the remote rejected the request.</summary>
    Task<bool[]?> ReadBitsAsync(int poll, CancellationToken ct);

    /// <summary>Read poll item <paramref name="poll"/> (a word range): Polls[poll].Count ushorts, or null when the remote rejected the request.</summary>
    Task<ushort[]?> ReadWordsAsync(int poll, CancellationToken ct);

    /// <summary>Write the current local bits of push item <paramref name="push"/> to the remote.</summary>
    Task WriteBitsAsync(int push, bool[] values, CancellationToken ct);

    /// <summary>Write the current local words of push item <paramref name="push"/> to the remote.</summary>
    Task WriteWordsAsync(int push, ushort[] values, CancellationToken ct);

    /// <summary>Recover after a communication error (drop the connection, drain the line, ...).</summary>
    Task ResetAsync(CancellationToken ct);

    /// <summary>Release the link (shutdown).</summary>
    void Close();
}
