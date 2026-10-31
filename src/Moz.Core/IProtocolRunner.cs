namespace Moz;

/// <summary>
/// One long-running communication block declared in the config: a Modbus TCP
/// server today; an RTU server, a master poller, or the OPC UA server later.
/// The CLI (or a future GUI) builds a list from config and awaits them all.
/// Loggers are constructor arguments of implementations, not interface members.
/// </summary>
public interface IProtocolRunner
{
    string Name { get; }

    /// <summary>
    /// Blocks until <paramref name="ct"/> is cancelled or a fatal error occurs.
    /// Returning normally on cancellation is expected behavior, not an error.
    /// </summary>
    Task RunAsync(CancellationToken ct);
}