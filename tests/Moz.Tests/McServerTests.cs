using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Client;
using Moz.Devices;
using Moz.Mc;

namespace Moz.Tests;

/// <summary>
/// MC protocol server (3E binary, TCP): the pure handler against a device
/// with the default map (D→holding, M→coil, X→discrete), then the runner
/// end to end with moz's own MC master as the client.
/// </summary>
public class McServerTests
{
    private const string Header = "500000FFFF03000C00"; // request header, data length 12 (no write data)

    private static Device NewDevice() => new("plc", bits: 256, readOnlyBits: 256, words: 256, readOnlyWords: 256);

    private static McOutcome Handle(Device device, string requestHex, IReadOnlyList<McMap>? maps = null)
    {
        var frame = Convert.FromHexString(requestHex);
        Assert.True(Mc3EServer.TryParseRequestHeader(frame.AsSpan(0, Mc3E.HeaderSize), out var length));
        Assert.Equal(frame.Length - Mc3E.HeaderSize, length);
        return Mc3EServer.Handle(frame.AsSpan(0, Mc3E.HeaderSize), frame.AsSpan(Mc3E.HeaderSize), device, maps ?? McMap.Defaults(device));
    }

    // --- handler --------------------------------------------------------------------------

    [Fact]
    public void Read_Words_FromDefaultMap()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 100, new ushort[] { 0x1234, 0x0002, 0xFFFF });
        var o = Handle(device, Header + "100001040000640000A80300");
        Assert.Equal("D00000FFFF030008000000" + "34120200FFFF", Convert.ToHexString(o.Response));
        Assert.Equal(("read", "D100", 3, 0), (o.Op, o.Target, o.Points, (int)o.EndCode));
    }

    [Fact]
    public void Read_Bits_FromDefaultMap()
    {
        var device = NewDevice();
        device.Seed(TableKind.Bits, 100, new[] { true, false, false, true, true });
        var o = Handle(device, Header + "100001040100640000900500");
        Assert.Equal("D00000FFFF030005000000" + "100110", Convert.ToHexString(o.Response));
        Assert.Equal(("read", "M100", 5), (o.Op, o.Target, o.Points));
    }

    [Fact]
    public void Write_Words_And_Bits_UpdateDevice()
    {
        var device = NewDevice();
        var o = Handle(device, "500000FFFF0300" + "1000" + "100001140000640000A80200" + "34127856");
        Assert.Equal("D00000FFFF030002000000", Convert.ToHexString(o.Response));
        Assert.Equal(("write", "D100", 2, 0), (o.Op, o.Target, o.Points, (int)o.EndCode));
        var w = new ushort[2];
        device.Read(TableKind.Words, 100, w);
        Assert.Equal([0x1234, 0x5678], w);

        o = Handle(device, "500000FFFF0300" + "0F00" + "100001140100640000900500" + "100110");
        Assert.Equal(0, o.EndCode);
        var b = new bool[6];
        device.Read(TableKind.Bits, 100, b);
        Assert.Equal([true, false, false, true, true, false], b);
    }

    [Fact]
    public void Routing_IsEchoed()
    {
        var device = NewDevice();
        var o = Handle(device, "50000102D00303" + "0C00" + "280001040000000000A80100");
        Assert.StartsWith("D0000102D00303", Convert.ToHexString(o.Response));
    }

    [Theory]
    [InlineData("100001040000640000B40300", 0xC05B, "unmapped device (W) with the default map")]
    [InlineData("100001040000640000FF0300", 0xC05B, "unknown device code")]
    [InlineData("100001040100640000A80300", 0xC05C, "bit units on a word device")]
    [InlineData("100001040000640000900300", 0xC05C, "word units on a bit device")]
    [InlineData("1000010400000000A0A80300", 0xC056, "head + points beyond the mapped range (D41984)")]
    [InlineData("100001040000FE0000A80300", 0xC056, "head 254 + 3 > 256")]
    [InlineData("100001040000640000A80000", 0xC051, "zero points")]
    [InlineData("100001040000640000A8C103", 0xC051, "961 words")]
    [InlineData("100001040100640000902C1C", 0xC051, "7212 bits")]
    [InlineData("100002040000640000A80300", 0xC059, "unknown command 0402")]
    [InlineData("100001040200640000A80300", 0xC059, "unknown subcommand 0002")]
    [InlineData("100001140000640000A80200" + "3412", 0xC061, "write data shorter than points")]
    [InlineData("10000104", 0xC061, "body shorter than a command")]
    public void Errors_EndCodes(string bodyHex, int endCode, string why)
    {
        var device = NewDevice();
        int length = bodyHex.Length / 2;
        var header = "500000FFFF0300" + $"{length & 0xFF:X2}{length >> 8:X2}";
        var o = Handle(device, header + bodyHex);
        Assert.True(endCode == o.EndCode, $"{why}: expected {endCode:X4}, got {o.EndCode:X4}");
        // error response: routing echo + length 0B + end code + error information (routing + command + subcommand)
        Assert.Equal(Mc3E.HeaderSize + 11, o.Response.Length);
        Assert.Equal((byte)endCode, o.Response[9]);
        Assert.Equal((byte)(endCode >> 8), o.Response[10]);
    }

    [Fact]
    public void Write_ToReadOnlyTable_IsRejected_C05C()
    {
        var device = NewDevice(); // X → read_only_bits inputs (read-only for peers)
        var o = Handle(device, "500000FFFF0300" + "0D00" + "100001140100" + "0A0000" + "9C" + "0100" + "10");
        Assert.Equal(0xC05C, o.EndCode);
        Assert.False(device.ReadBit(TableKind.ReadOnlyBits, 0x0A));
    }

    [Fact]
    public void UserMap_Overrides_And_AddsDevices()
    {
        var device = NewDevice();
        var maps = McMap.Merge(device,
        [
            new McMap(Code("W"), TableKind.ReadOnlyWords, 10, 20),    // W0..W19 → read_only_words 10..29 (read-only)
            new McMap(Code("D"), TableKind.Words, 100, 50), // D0 → words 100
        ]);
        Assert.Contains(maps, m => m.Device.Code == "M");             // defaults kept
        Assert.Contains(maps, m => m.Device.Code == "X");
        Assert.Single(maps, m => m.Device.Code == "D");
        device.Seed(TableKind.ReadOnlyWords, 11, new ushort[] { 0xBEEF });
        device.Seed(TableKind.Words, 100, new ushort[] { 0xCAFE });

        var o = Handle(device, Header + "100001040000010000B40100", maps);        // W1
        Assert.Equal("D00000FFFF030004000000EFBE", Convert.ToHexString(o.Response));
        o = Handle(device, Header + "100001040000000000A80100", maps);            // D0 → words 100
        Assert.Equal("D00000FFFF030004000000FECA", Convert.ToHexString(o.Response));
        o = Handle(device, Header + "100001040000320000A80100", maps);            // D50: beyond count 50
        Assert.Equal(0xC056, o.EndCode);
    }

    [Fact]
    public void RequestHeader_Rejects_WrongSubheader_AndShortLength()
    {
        Assert.False(Mc3EServer.TryParseRequestHeader(Convert.FromHexString("D00000FFFF03000C00"), out _));
        Assert.False(Mc3EServer.TryParseRequestHeader(Convert.FromHexString("500000FFFF03000000"), out _));
        Assert.False(Mc3EServer.TryParseRequestHeader(Convert.FromHexString("500000FFFF0300"), out _));
    }

    // --- runner (TCP) ------------------------------------------------------------------------

    [Fact]
    public async Task Server_ServesMozMaster_PollAndPush()
    {
        var plc = NewDevice();
        plc.Seed(TableKind.Words, 0, new ushort[] { 0x1111, 0x2222 });
        var provider = new CapturingProvider();
        var server = new McServer("mc-srv", new IPEndPoint(IPAddress.Loopback, 0), plc, McMap.Defaults(plc), provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        while (server.BoundEndpoint is null)
        {
            await Task.Delay(10);
        }

        var local = new Device("local", bits: 32, words: 32);
        McEntry[] polls = [Entry("D0", 2, TableKind.Words, 0), Entry("X5", 3, TableKind.Bits, 0)];
        McEntry[] pushes = [Entry("M10", 3, TableKind.Bits, 8)];
        var remote = new McRemote("mc", server.BoundEndpoint, 2000, new Mc3EOptions(), polls, pushes, NullLogger.Instance);
        var poller = new ClientPoller("mc", remote, 50, local, NullLogger.Instance);
        var pollerTask = poller.RunAsync(cts.Token);
        await Task.Delay(100);
        local.Write(TableKind.Bits, 8, new[] { true, false, true });
        await Task.Delay(300);
        Assert.False(poller.HasPendingPush);

        var w = new ushort[2];
        local.Read(TableKind.Words, 0, w);
        Assert.Equal([0x1111, 0x2222], w);
        Assert.True(plc.ReadBit(TableKind.Bits, 10));
        Assert.False(plc.ReadBit(TableKind.Bits, 11));
        Assert.True(plc.ReadBit(TableKind.Bits, 12));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("target=D0") && e.Message.Contains("qty=2") && e.Message.Contains("result=ok"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=write") && e.Message.Contains("target=M10"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnAccepted.Id);

        await cts.CancelAsync();
        await pollerTask;
        await run;
    }

    [Fact]
    public async Task Server_BadSubheader_ClosesConnection_LogsMalformed()
    {
        var plc = NewDevice();
        var provider = new CapturingProvider();
        var server = new McServer("mc-srv", new IPEndPoint(IPAddress.Loopback, 0), plc, McMap.Defaults(plc), provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        while (server.BoundEndpoint is null)
        {
            await Task.Delay(10);
        }

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint);
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString("D00000FFFF03000C00100001040000640000A80300"));
        var buf = new byte[32];
        using var readCts = new CancellationTokenSource(2000);
        try
        {
            int n = await stream.ReadAsync(buf, readCts.Token);
            Assert.Equal(0, n); // closed cleanly, no answer
        }
        catch (IOException)
        {
            // reset by peer (unread bytes were pending when the server closed): also "closed, no answer"
        }
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.MalformedFrame.Id);

        await cts.CancelAsync();
        await run;
    }

    /// <summary>Log lines are written after the response is sent, so give the server a moment before asserting on them.</summary>
    private static async Task ExpectLogAsync(CapturingProvider provider, Predicate<CapturingProvider.Captured> predicate)
    {
        for (int i = 0; i < 50 && !provider.Entries.Exists(predicate); i++)
        {
            await Task.Delay(20);
        }
        Assert.Contains(provider.Entries, predicate);
    }

    private static McDevice Code(string code)
    {
        Assert.True(McDevice.TryFromCode(code, out var device));
        return device;
    }

    private static McEntry Entry(string device, int count, TableKind table, int localOffset)
    {
        Assert.True(McDevice.TryParse(device, out var dev, out var address));
        return new McEntry(dev, address, count, table, localOffset);
    }
}
