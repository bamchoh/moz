using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Client;
using Moz.Devices;
using Moz.Mc;

namespace Moz.Tests;

/// <summary>
/// The generic client loop over an MC protocol (3E binary, TCP) remote,
/// against a minimal MC server written independently of the codec (word
/// devices live in holding registers, bit devices in coils).
/// </summary>
public class McRemoteTests
{
    private static McRemote Remote(int port, McEntry[] polls, McEntry[]? pushes = null,
        Microsoft.Extensions.Logging.ILogger? logger = null, int timeoutMs = 2000) =>
        new McRemote("mc", new IPEndPoint(IPAddress.Loopback, port), timeoutMs, new Mc3EOptions(), polls, pushes ?? [], logger ?? NullLogger.Instance);

    private static McEntry Entry(string device, int count, TableKind table, int localOffset)
    {
        Assert.True(McDevice.TryParse(device, out var dev, out var address));
        return new McEntry(dev, address, count, table, localOffset);
    }

    [Fact]
    public async Task Poll_MirrorsWordsAndBits()
    {
        var plc = new Device("plc", bits: 64, words: 256);
        plc.Seed(TableKind.Words, 100, new ushort[] { 0x1234, 0x5678, 0x9ABC });
        plc.Seed(TableKind.Bits, 0, new[] { true, false, true, true, false, false, false, true });
        await using var server = await McTestServer.Start(plc);
        var local = new Device("local", readOnlyBits: 32, words: 32);
        var provider = new CapturingProvider();
        var remote = Remote(server.Port, [Entry("D100", 3, TableKind.Words, 0), Entry("M0", 8, TableKind.ReadOnlyBits, 4)],
            logger: provider.CreateLogger("mc"));
        var poller = new ClientPoller("mc", remote, 50, local, provider.CreateLogger("mc"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(300);
        Assert.True(poller.IsConnected);
        await cts.CancelAsync();
        await task;

        var w = new ushort[3];
        local.Read(TableKind.Words, 0, w);
        Assert.Equal([0x1234, 0x5678, 0x9ABC], w);
        var b = new bool[8];
        local.Read(TableKind.ReadOnlyBits, 4, b);
        Assert.Equal([true, false, true, true, false, false, false, true], b);
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("target=D100") && e.Message.Contains("qty=3") && e.Message.Contains("result=ok"));
    }

    [Fact]
    public async Task Push_WritesWordsAndBits()
    {
        var plc = new Device("plc", bits: 64, words: 256);
        await using var server = await McTestServer.Start(plc);
        var local = new Device("local", bits: 32, words: 32);
        var remote = Remote(server.Port, [], [Entry("D10", 2, TableKind.Words, 0), Entry("M16", 5, TableKind.Bits, 0)]);
        var poller = new ClientPoller("mc", remote, 50, local, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(100);
        local.Write(TableKind.Words, 0, new ushort[] { 0xBEEF, 0x0001 });
        local.Write(TableKind.Bits, 0, new[] { true, false, false, true, true });
        await Task.Delay(300);
        Assert.False(poller.HasPendingPush);
        await cts.CancelAsync();
        await task;

        var w = new ushort[2];
        plc.Read(TableKind.Words, 10, w);
        Assert.Equal([0xBEEF, 0x0001], w);
        var b = new bool[5];
        plc.Read(TableKind.Bits, 16, b);
        Assert.Equal([true, false, false, true, true], b);
    }

    [Fact]
    public async Task EndCode_IsRejection_Logged_NotRetried_LoopContinues()
    {
        var plc = new Device("plc", bits: 64, words: 256);
        plc.Seed(TableKind.Words, 0, new ushort[] { 0x4242 });
        await using var server = await McTestServer.Start(plc);
        var local = new Device("local", words: 32);
        var provider = new CapturingProvider();
        var remote = Remote(server.Port,
            [Entry("D1000", 1, TableKind.Words, 0), Entry("D0", 1, TableKind.Words, 1)],   // D1000 is beyond the server's 256 words
            [Entry("D1000", 1, TableKind.Words, 2)],
            provider.CreateLogger("mc"));
        var poller = new ClientPoller("mc", remote, 50, local, provider.CreateLogger("mc"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(100);
        local.Write(TableKind.Words, 2, new ushort[] { 1 });
        await Task.Delay(300);
        Assert.True(poller.IsConnected);
        Assert.False(poller.HasPendingPush);          // rejected = delivered
        await cts.CancelAsync();
        await task;

        var w = new ushort[2];
        local.Read(TableKind.Words, 0, w);
        Assert.Equal([0, 0x4242], w);                 // D1000 mirrored nothing, D0 kept working on the same connection
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("target=D1000") && e.Message.Contains("err=C056"));
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=write") && e.Message.Contains("target=D1000") && e.Message.Contains("err=C056"));
        Assert.DoesNotContain(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id);
    }

    [Fact]
    public async Task SilentServer_TimesOut_IsCommError_Reconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); // accepts, never answers
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var local = new Device("local", words: 32);
        var provider = new CapturingProvider();
        var remote = Remote(port, [Entry("D0", 1, TableKind.Words, 0)], logger: provider.CreateLogger("mc"), timeoutMs: 200);
        var poller = new ClientPoller("mc", remote, 100, local, provider.CreateLogger("mc"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(700);
        await cts.CancelAsync();
        await task;
        listener.Stop();

        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id && e.Message.Contains("timed out"));
        Assert.True(provider.Entries.Count(e => e.EventId == Moz.Logging.CommLog.EventIds.ConnAccepted.Id) >= 2); // reset = reconnect
    }
}

/// <summary>
/// Minimal MC (3E binary) server for tests, parsed by hand (not through the
/// codec under test). Batch read 0401 / batch write 1401 on one device per
/// request; word devices map to holding registers, bit devices to coils;
/// out-of-range → end code C056, anything else → C059.
/// </summary>
internal sealed class McTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Device _device;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private McTestServer(TcpListener listener, Device device)
    {
        _listener = listener;
        _device = device;
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<McTestServer> Start(Device device)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new McTestServer(listener, device));
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[9];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, _cts.Token);
                    if (header[0] != 0x50 || header[1] != 0x00)
                    {
                        return;
                    }
                    int length = header[7] | (header[8] << 8);
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, _cts.Token);
                    var resp = Handle(header, body);
                    await stream.WriteAsync(resp, _cts.Token);
                }
            }
            catch (Exception)
            {
                // peer closed / shutdown
            }
        }
    }

    private static readonly Dictionary<byte, bool> BitDevices = new()
    {
        [0x90] = true, [0x9C] = true, [0x9D] = true, [0xA0] = true, // M, X, Y, B
        [0xA8] = false, [0xB4] = false, [0xAF] = false,             // D, W, R
    };

    private byte[] Handle(byte[] header, byte[] body)
    {
        // body: timer(2) cmd(2) sub(2) head(3) code(1) points(2) [data]
        int cmd = body[2] | (body[3] << 8);
        int sub = body[4] | (body[5] << 8);
        int head = body[6] | (body[7] << 8) | (body[8] << 16);
        byte code = body[9];
        int points = body[10] | (body[11] << 8);
        if (!BitDevices.TryGetValue(code, out var isBit) || (sub != (isBit ? 1 : 0)))
        {
            return Response(header, 0xC059, [], body);
        }
        var table = isBit ? TableKind.Bits : TableKind.Words;
        int size = _device.Length(table);
        if (points < 1 || head + points > size)
        {
            return Response(header, 0xC056, [], body);
        }
        switch (cmd)
        {
            case 0x0401:
                if (isBit)
                {
                    var bits = new bool[points];
                    _device.Read(table, head, bits);
                    var data = new byte[(points + 1) / 2];
                    for (int i = 0; i < points; i++)
                    {
                        if (bits[i])
                        {
                            data[i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
                        }
                    }
                    return Response(header, 0, data, body);
                }
                else
                {
                    var words = new ushort[points];
                    _device.Read(table, head, words);
                    var data = new byte[points * 2];
                    for (int i = 0; i < points; i++)
                    {
                        data[i * 2] = (byte)words[i];
                        data[i * 2 + 1] = (byte)(words[i] >> 8);
                    }
                    return Response(header, 0, data, body);
                }
            case 0x1401:
                if (isBit)
                {
                    var bits = new bool[points];
                    for (int i = 0; i < points; i++)
                    {
                        bits[i] = (body[12 + i / 2] & (i % 2 == 0 ? 0x10 : 0x01)) != 0;
                    }
                    _device.Write(table, head, bits);
                }
                else
                {
                    var words = new ushort[points];
                    for (int i = 0; i < points; i++)
                    {
                        words[i] = (ushort)(body[12 + i * 2] | (body[13 + i * 2] << 8));
                    }
                    _device.Write(table, head, words);
                }
                return Response(header, 0, [], body);
            default:
                return Response(header, 0xC059, [], body);
        }
    }

    private static byte[] Response(byte[] reqHeader, int endCode, byte[] data, byte[] reqBody)
    {
        // D000 | net pc io(2) st (echoed) | len(2) | endcode(2) | data  (errors: + net pc io st cmd sub)
        var extra = endCode == 0 ? [] : new[] { reqHeader[2], reqHeader[3], reqHeader[4], reqHeader[5], reqHeader[6], reqBody[2], reqBody[3], reqBody[4], reqBody[5] };
        int length = 2 + data.Length + extra.Length;
        var resp = new byte[9 + length];
        resp[0] = 0xD0;
        resp[1] = 0x00;
        Array.Copy(reqHeader, 2, resp, 2, 5);
        resp[7] = (byte)length;
        resp[8] = (byte)(length >> 8);
        resp[9] = (byte)endCode;
        resp[10] = (byte)(endCode >> 8);
        data.CopyTo(resp, 11);
        extra.CopyTo(resp, 11 + data.Length);
        return resp;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch { }
    }
}
