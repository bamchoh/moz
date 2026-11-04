namespace Moz;

/// <summary>
/// One long-running communication block declared in the config: a Modbus TCP
/// server, the RTU server, the master poller, scripts and the OPC UA server.
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

    /// <summary>
    /// Completes once the runner is serving (a server is listening); faults
    /// when startup failed. Clients and scripts start after every server's
    /// Ready (<see cref="RunnerHost"/>). Default: ready immediately.
    /// </summary>
    Task Ready => Task.CompletedTask;
}