using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.Modbus.Client;

namespace Moz.Tests;

/// <summary>
/// The mirror: moz as master polls a remote slave (moz's own TCP server)
/// and copies the answers into a local device. Variables/scripts see the
/// mirror without knowing anything about the remote.
/// </summary>
public class ClientPollerTests
{
    private static readonly int[][] StandardPolls = [[3, 0, 2, 0]]; // fc, remoteOffset, count, localOffset (holding→holding)

    [Fact]
    public async Task Poll_MirrorsRemoteHoldingRegisters()
    {
        var remote = await TestServer.Start(); // default device: holding 0..1 = 0x1234, 0xABCD
        var local = new Device("local", holdingRegisters: 100);
        var poller = new ModbusClientPoller("p", IPEndPointOf(remote.Port), remoteUnit: 1,
            intervalMs: 50, timeoutMs: 2000, local, Entries(StandardPolls), NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();
        await task;

        var w = new ushort[2];
        local.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal((ushort)0x1234, w[0]);
        Assert.Equal((ushort)0xABCD, w[1]);
    }

    [Fact]
    public async Task Poll_MirrorsBits_IntoLocalCoilTable()
    {
        var remote = await TestServer.Start(); // discrete seeded [true,false,true]
        var local = new Device("local");
        // local_kind override: remote discrete (fc2) → local coil table
        var polls = new[] { new PollEntry(2, 0, 3, TableKind.Coil, 10) };
        var poller = new ModbusClientPoller("p", IPEndPointOf(remote.Port), 1,
            50, 2000, local, polls, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();
        await task;

        Assert.True(local.ReadBit(TableKind.Coil, 10));
        Assert.False(local.ReadBit(TableKind.Coil, 11));
        Assert.True(local.ReadBit(TableKind.Coil, 12));
    }

    [Fact]
    public async Task Poll_RemoteRangeError_QuietlySkips_KeepsPolling()
    {
        var remote = await TestServer.Start(device: new Device("t", holdingRegisters: 10));
        var local = new Device("local", holdingRegisters: 100);
        var polls = new[] { new PollEntry(3, 100, 1, TableKind.HoldingRegister, 0) }; // beyond remote
        var poller = new ModbusClientPoller("p", IPEndPointOf(remote.Port), 1,
            50, 2000, local, polls, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(200);
        await cts.CancelAsync();
        await task;

        // nothing mirrored (exception response), but the poller survived and kept looping
        var w = new ushort[1];
        local.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal((ushort)0, w[0]);
    }

    /// <summary>
    /// Connect-failure recovery. The "down" port is one we bound and closed —
    /// reliably refused (RST) on every platform, unlike a never-bound port,
    /// where some environments hang instead of refusing. The server then comes
    /// back up on the SAME port.
    /// </summary>
    [Fact]
    public async Task Poll_RemoteDownThenUp_Recovers()
    {
        var down = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        down.Start();
        var port = ((System.Net.IPEndPoint)down.LocalEndpoint).Port;
        down.Stop(); // from here the port refuses connections

        var local = new Device("local", holdingRegisters: 100);
        var poller = new ModbusClientPoller("p", new IPEndPoint(IPAddress.Loopback, port), 1,
            50, 500, local, Entries(StandardPolls), NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token); // failures logged, loop survives
        await Task.Delay(150);

        var remote = await TestServer.Start(port: port); // now it appears on the same port
        await Task.Delay(400);                           // next tick should mirror
        await cts.CancelAsync();
        await task;

        var w = new ushort[1];
        local.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal((ushort)0x1234, w[0]);
        await remote.DisposeAsync();
    }

    private static IPEndPoint IPEndPointOf(ushort port) => new(IPAddress.Loopback, port);

    private static PollEntry[] Entries(int[][] polls) =>
        polls.Select(p => new PollEntry(p[0], p[1], p[2], TableFor(p[0]), p[3])).ToArray();

    private static TableKind TableFor(int fc) => fc switch
    {
        1 => TableKind.Coil,
        2 => TableKind.DiscreteInput,
        3 => TableKind.HoldingRegister,
        4 => TableKind.InputRegister,
        _ => throw new ArgumentOutOfRangeException(nameof(fc)),
    };
}