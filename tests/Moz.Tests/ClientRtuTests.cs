using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.Modbus;
using Moz.Modbus.Client;
using Moz.Modbus.Rtu;

namespace Moz.Tests;

/// <summary>
/// moz as Modbus RTU master: the same poll/push loop as the TCP master, over
/// a serial-like Stream (in-memory pipe here; a serial port in the CLI).
/// The remote is moz's own RTU server, or a hand-rolled responder for the
/// error cases (silence, bad CRC, wrong address, truncated frame).
/// </summary>
public class ClientRtuTests
{
    private static RtuMasterLink Link(SerialBus bus, int timeoutMs = 300) =>
        new(() => bus.MasterStream, "pipe", timeoutMs, idleGapMs: 50);

    [Fact]
    public async Task Poll_MirrorsRemoteOverSerial()
    {
        var remote = TestServer.MakeDefaultDevice(); // holding 0..1 = 0x1234, 0xABCD; discrete [T,F,T]
        await using var bus = new SerialBus();
        await using var server = await RtuTestServer.Start(bus.SlaveStream, remote);
        var local = new Device("local", holdingRegisters: 100, discreteInputs: 100);
        PollEntry[] polls = [new(3, 0, 2, TableKind.HoldingRegister, 0), new(2, 0, 3, TableKind.DiscreteInput, 10)];
        var poller = new ModbusClientPoller("p", Link(bus), 1, 50, local, polls, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(400);
        Assert.True(poller.IsConnected); // the slave answered
        await cts.CancelAsync();
        await task;

        var w = new ushort[2];
        local.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal([0x1234, 0xABCD], w);
        Assert.True(local.ReadBit(TableKind.DiscreteInput, 10));
        Assert.False(local.ReadBit(TableKind.DiscreteInput, 11));
        Assert.True(local.ReadBit(TableKind.DiscreteInput, 12));
    }

    [Fact]
    public async Task Push_WritesRemoteOverSerial()
    {
        var remote = new Device("remote", coils: 100, holdingRegisters: 100);
        await using var bus = new SerialBus();
        await using var server = await RtuTestServer.Start(bus.SlaveStream, remote);
        var local = new Device("local", coils: 100, holdingRegisters: 100);
        PushEntry[] pushes = [new(16, TableKind.HoldingRegister, 10, 2, 20), new(5, TableKind.Coil, 3, 1, 7)];
        var provider = new CapturingProvider();
        var poller = new ModbusClientPoller("p", Link(bus), 1, 50, local, [], provider.CreateLogger("p"), pushes);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(100);
        local.Write(TableKind.HoldingRegister, 10, new ushort[] { 0x1111, 0x2222 });
        local.WriteBit(TableKind.Coil, 3, true);
        await Task.Delay(400);
        Assert.False(poller.HasPendingPush, string.Join("; ", provider.Entries.Select(e => e.Message)));
        await cts.CancelAsync();
        await task;

        var w = new ushort[2];
        remote.Read(TableKind.HoldingRegister, 20, w);
        Assert.Equal([0x1111, 0x2222], w);
        Assert.True(remote.ReadBit(TableKind.Coil, 7));
    }

    [Fact]
    public async Task SilentSlave_TimesOut_KeepsPushPending_ThenDelivers()
    {
        var remote = new Device("remote", holdingRegisters: 100);
        await using var bus = new SerialBus();
        var responder = new RtuResponder(bus.SlaveStream, remote) { Silent = true };
        var local = new Device("local", holdingRegisters: 100);
        var provider = new CapturingProvider();
        PollEntry[] polls = [new(3, 0, 1, TableKind.HoldingRegister, 0)];
        PushEntry[] pushes = [new(6, TableKind.HoldingRegister, 5, 1, 5)];
        var poller = new ModbusClientPoller("p", Link(bus, timeoutMs: 200), 1, 100, local, polls, provider.CreateLogger("p"), pushes);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        local.Write(TableKind.HoldingRegister, 5, new ushort[] { 0x5A5A });
        await Task.Delay(500);
        Assert.False(poller.IsConnected); // nothing answered yet
        Assert.True(poller.HasPendingPush);
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id && e.Message.Contains("timed out"));

        responder.Silent = false;
        await Task.Delay(600);
        Assert.True(poller.IsConnected);
        Assert.False(poller.HasPendingPush);
        await cts.CancelAsync();
        await task;
        await responder.DisposeAsync();

        var w = new ushort[1];
        remote.Read(TableKind.HoldingRegister, 5, w);
        Assert.Equal((ushort)0x5A5A, w[0]);
    }

    [Theory]
    [InlineData("crc", "CRC")]
    [InlineData("address", "address")]
    [InlineData("truncated", "incomplete")]
    public async Task BadResponse_IsCommError_NextPollSucceeds(string kind, string logText)
    {
        var remote = new Device("remote", holdingRegisters: 100);
        remote.Seed(TableKind.HoldingRegister, 0, new ushort[] { 0x4242 });
        await using var bus = new SerialBus();
        int mangled = 0;
        var responder = new RtuResponder(bus.SlaveStream, remote)
        {
            Mangle = adu =>
            {
                if (mangled++ > 0)
                {
                    return adu; // only the first response is corrupted
                }
                switch (kind)
                {
                    case "crc":
                        adu[^1] ^= 0xFF;
                        return adu;
                    case "address":
                        return Rtu.Frame(2, adu.AsSpan(1, adu.Length - 3)); // same PDU, from slave 2
                    default:
                        return adu[..^2]; // CRC never arrives: idle gap ends the frame
                }
            },
        };
        var local = new Device("local", holdingRegisters: 100);
        var provider = new CapturingProvider();
        PollEntry[] polls = [new(3, 0, 1, TableKind.HoldingRegister, 0)];
        var poller = new ModbusClientPoller("p", Link(bus), 1, 100, local, polls, provider.CreateLogger("p"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(700);
        await cts.CancelAsync();
        await task;
        await responder.DisposeAsync();

        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id && e.Message.Contains(logText));
        var w = new ushort[1];
        local.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal((ushort)0x4242, w[0]); // the next poll on the same port worked
        Assert.True(mangled >= 2);
    }
}

/// <summary>
/// Serial slave stand-in for error-path tests: parses request frames (no
/// idle timing: bytes arrive instantly on a pipe), answers from a device
/// through the shared codec, and lets the test mangle or swallow the answer.
/// </summary>
internal sealed class RtuResponder : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Device _device;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public RtuResponder(Stream stream, Device device)
    {
        _stream = stream;
        _device = device;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>True = consume requests, answer nothing (slave absent).</summary>
    public volatile bool Silent;

    /// <summary>Transform the response ADU before it is sent; null = send nothing.</summary>
    public Func<byte[], byte[]?> Mangle { get; set; } = adu => adu;

    private async Task LoopAsync()
    {
        var buf = new byte[Rtu.AduMaxSize];
        int length = 0;
        var one = new byte[1];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (await _stream.ReadAsync(one, _cts.Token) == 0)
                {
                    return;
                }
                buf[length++] = one[0];
                if (length >= buf.Length)
                {
                    length = 0;
                    continue;
                }
                if (!Rtu.TryParseFrame(buf.AsSpan(0, length), out var addr, out var pdu))
                {
                    continue;
                }
                length = 0;
                if (Silent)
                {
                    continue;
                }
                var resp = new byte[ModbusLimits.PduMaxLength + 2];
                var outcome = ModbusPdu.Handle(pdu, _device, resp);
                var adu = Mangle(Rtu.Frame(addr, resp.AsSpan(0, outcome.Length)));
                if (adu is not null)
                {
                    await _stream.WriteAsync(adu, _cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _loop; } catch { }
    }
}
