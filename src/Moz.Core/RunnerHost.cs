using Microsoft.Extensions.Logging;

namespace Moz;

/// <summary>
/// Runs the configured runners with a startup order: servers first, then —
/// once every server reports <see cref="IProtocolRunner.Ready"/> or the
/// timeout passes — the clients and scripts. This keeps a client that talks
/// to a server in the same process (self-poll) from logging connection
/// failures while that server is still starting. The first fatal failure
/// cancels the rest and is rethrown.
/// </summary>
public static class RunnerHost
{
    public static async Task RunAsync(IReadOnlyList<IProtocolRunner> servers, IReadOnlyList<IProtocolRunner> others,
        TimeSpan readyTimeout, ILogger logger, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<Task>(servers.Count + others.Count);
        foreach (var server in servers)
        {
            tasks.Add(server.RunAsync(cts.Token));
        }
        logger.LogInformation("starting {Count} server(s)", servers.Count);

        if (servers.Count > 0 && others.Count > 0)
        {
            try
            {
                // a faulted Ready is a fatal runner: its RunAsync task carries the error
                await Task.WhenAll(servers.Select(s => s.Ready.ContinueWith(_ => { }, TaskScheduler.Default)))
                    .WaitAsync(readyTimeout, cts.Token);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("not every server became ready within {Timeout}; starting clients and scripts anyway", readyTimeout);
            }
            catch (OperationCanceledException)
            {
                // shutdown during startup: fall through, the server tasks are winding down
            }
        }

        if (!cts.IsCancellationRequested)
        {
            foreach (var other in others)
            {
                tasks.Add(other.RunAsync(cts.Token));
            }
            logger.LogInformation("starting {Count} client/script runner(s)", others.Count);
        }

        // Task.WhenAll would wait for EVERY runner even after one faulted; a
        // fatal failure must stop the others right away, so watch them one by one
        Exception? first = null;
        var remaining = new List<Task>(tasks);
        while (remaining.Count > 0)
        {
            var done = await Task.WhenAny(remaining);
            remaining.Remove(done);
            if (done.IsFaulted && first is null)
            {
                first = done.Exception!.InnerExceptions.Count == 1 ? done.Exception.InnerExceptions[0] : done.Exception;
                await cts.CancelAsync();
            }
        }
        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }
}
