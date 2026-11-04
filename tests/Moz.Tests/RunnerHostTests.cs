using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moz;
using Moz.Devices;
using Moz.Mc;
using Moz.Modbus.Tcp;

namespace Moz.Tests;

/// <summary>
/// Startup order: servers first, and clients/scripts only once every server
/// reports <see cref="IProtocolRunner.Ready"/> (bounded by a timeout), so a
/// client in the same process does not log connection failures against a
/// server that is still starting.
/// </summary>
public class RunnerHostTests
{
    private sealed class FakeRunner(string name, TimeSpan readyAfter, bool neverReady = false, Exception? fail = null) : IProtocolRunner
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long StartedAt = -1;
        public bool Stopped;
        public string Name => name;
        public Task Ready => _ready.Task;

        public async Task RunAsync(CancellationToken ct)
        {
            StartedAt = Environment.TickCount64;
            if (fail is not null)
            {
                throw fail;
            }
            if (!neverReady)
            {
                await Task.Delay(readyAfter, ct);
                _ready.TrySetResult();
            }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) { }
            Stopped = true;
        }
    }

    [Fact]
    public async Task Others_StartAfter_AllServersReady()
    {
        var s1 = new FakeRunner("s1", TimeSpan.FromMilliseconds(150));
        var s2 = new FakeRunner("s2", TimeSpan.FromMilliseconds(300));
        var client = new FakeRunner("c", TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var run = RunnerHost.RunAsync([s1, s2], [client], TimeSpan.FromSeconds(5), NullLogger.Instance, cts.Token);
        await Task.Delay(100);
        Assert.True(s1.StartedAt >= 0 && s2.StartedAt >= 0);
        Assert.Equal(-1, client.StartedAt);           // not yet: s2 is still starting
        await Task.Delay(400);
        Assert.True(client.StartedAt >= s2.StartedAt + 250, "client must start after the slowest server is ready");
        await cts.CancelAsync();
        await run;
        Assert.True(s1.Stopped && s2.Stopped && client.Stopped);
    }

    [Fact]
    public async Task ReadyTimeout_StartsOthersAnyway()
    {
        var stuck = new FakeRunner("stuck", TimeSpan.Zero, neverReady: true);
        var client = new FakeRunner("c", TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        var run = RunnerHost.RunAsync([stuck], [client], TimeSpan.FromMilliseconds(200), NullLogger.Instance, cts.Token);
        await Task.Delay(600);
        Assert.True(client.StartedAt >= 0);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task FatalRunner_StopsTheRest_AndThrows()
    {
        var server = new FakeRunner("s", TimeSpan.Zero);
        var bad = new FakeRunner("bad", TimeSpan.Zero, fail: new InvalidOperationException("boom"));
        using var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunnerHost.RunAsync([server], [bad], TimeSpan.FromSeconds(1), NullLogger.Instance, cts.Token));
        Assert.Equal("boom", ex.Message);
        Assert.True(server.Stopped);
    }

    [Fact]
    public async Task DefaultReady_IsCompleted()
    {
        IProtocolRunner plain = new PlainRunner();
        Assert.True(plain.Ready.IsCompletedSuccessfully);
    }

    private sealed class PlainRunner : IProtocolRunner
    {
        public string Name => "plain";
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task TcpServers_ReportReady_OnceListening()
    {
        var device = new Device("d", words: 8, bits: 8);
        var modbus = new ModbusTcpServer("m", new IPEndPoint(IPAddress.Loopback, 0), device, null, NullLogger.Instance);
        var mc = new McServer("mc", new IPEndPoint(IPAddress.Loopback, 0), device, McMap.Defaults(device), NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        Assert.False(modbus.Ready.IsCompleted);
        Assert.False(mc.Ready.IsCompleted);
        var t1 = modbus.RunAsync(cts.Token);
        var t2 = mc.RunAsync(cts.Token);
        await Task.WhenAll(modbus.Ready, mc.Ready).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(modbus.BoundEndpoint);
        Assert.NotNull(mc.BoundEndpoint);
        await cts.CancelAsync();
        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task TcpServer_ListenFailure_FaultsReady()
    {
        var device = new Device("d", words: 8);
        var first = new ModbusTcpServer("m1", new IPEndPoint(IPAddress.Loopback, 0), device, null, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        var t1 = first.RunAsync(cts.Token);
        await first.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        var second = new ModbusTcpServer("m2", first.BoundEndpoint!, device, null, NullLogger.Instance);
        var t2 = second.RunAsync(cts.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => second.Ready.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<Exception>(() => t2);
        await cts.CancelAsync();
        await t1;
    }
}
