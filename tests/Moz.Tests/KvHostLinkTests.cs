using System.Net;
using System.Net.Sockets;
using System.Text;
using Moz.Devices;
using Moz.Kv;

namespace Moz.Tests;

/// <summary>
/// Keyence KV host link server (phase 21): device notation, the pure line
/// handler against a device (default map DM → words, R → bits, user maps
/// for the rest), and the TCP runner with a hand-written client.
/// </summary>
public class KvHostLinkTests
{
    private static Device NewDevice() => new("plc", bits: 256, readOnlyBits: 256, words: 256, readOnlyWords: 256);

    /// <summary>MR 0..255 bits → readOnlyBits 0..255, EM 0..255 → readOnlyWords, W 0..99 → words 100..199, plus the defaults.</summary>
    private static KvMap[] UserMaps(Device device) => KvMap.Merge(device,
    [
        new KvMap(Dev("MR"), TableKind.ReadOnlyBits, 0, 256),
        new KvMap(Dev("EM"), TableKind.ReadOnlyWords, 0, 256),
        new KvMap(Dev("W"), TableKind.Words, 100, 100),
    ]);

    private static KvDevice Dev(string code)
    {
        Assert.True(KvDevice.TryFromCode(code, out var device), code);
        return device;
    }

    private static ushort Word(Device device, int index)
    {
        var one = new ushort[1];
        device.Read(TableKind.Words, index, one);
        return one[0];
    }

    private static KvOutcome Handle(Device device, string line, IReadOnlyList<KvMap>? maps = null) =>
        KvHostLink.Handle(line, device, maps ?? KvMap.Defaults(device));

    // --- device notation --------------------------------------------------------------------------

    [Theory]
    [InlineData("DM100", "DM", 100, 0, KvFormat.U)]
    [InlineData("dm100.s", "DM", 100, 0, KvFormat.S)]
    [InlineData("DM0.D", "DM", 0, 0, KvFormat.D)]
    [InlineData("EM5.L", "EM", 5, 0, KvFormat.L)]
    [InlineData("W10.H", "W", 10, 0, KvFormat.H)]
    [InlineData("ZF3", "ZF", 3, 0, KvFormat.U)]
    [InlineData("FM1", "FM", 1, 0, KvFormat.U)]
    [InlineData("R100", "R", 1, 0, KvFormat.U)]      // channel 1, bit 00
    [InlineData("R15", "R", 0, 15, KvFormat.U)]      // channel 0, bit 15
    [InlineData("MR1015", "MR", 10, 15, KvFormat.U)]
    [InlineData("LR0", "LR", 0, 0, KvFormat.U)]
    [InlineData("B1F", "B", 1, 15, KvFormat.U)]      // B is hexadecimal, 16 bits per channel
    [InlineData("B3FFF", "B", 0x3FF, 15, KvFormat.U)]
    public void Device_Parses(string text, string code, int channel, int bit, KvFormat format)
    {
        Assert.True(KvDevice.TryParse(text, out var device, out var address, out var parsedBit, out var parsedFormat));
        Assert.Equal((code, channel, bit, format), (device.Code, address, parsedBit, parsedFormat));
        Assert.Equal(code is "R" or "MR" or "LR" or "B", device.IsBit);
    }

    [Theory]
    [InlineData("R116")]       // bit 16
    [InlineData("R100.U")]     // no format on relays
    [InlineData("X0")]
    [InlineData("DM")]
    [InlineData("DM100.X")]
    [InlineData("DM-1")]
    [InlineData("B1G")]
    [InlineData("")]
    public void Device_Invalid_IsRejected(string text)
    {
        Assert.False(KvDevice.TryParse(text, out _, out _, out _, out _));
    }

    [Fact]
    public void Device_Format()
    {
        Assert.Equal("DM100", Dev("DM").Format(100, 0));
        Assert.Equal("R1015", Dev("R").Format(10, 15));
        Assert.Equal("R5", Dev("R").Format(0, 5));
        Assert.Equal("B1F", Dev("B").Format(1, 15));
    }

    // --- control and status commands ----------------------------------------------------------------

    [Theory]
    [InlineData("CR", "CC")]
    [InlineData("CQ", "CF")]
    [InlineData("?K", "55")]
    [InlineData("?M", "1")]
    [InlineData("cr", "CC")]
    public void Control_Commands(string line, string response)
    {
        var o = Handle(NewDevice(), line);
        Assert.Equal((response, null), (o.Response, o.Error));
    }

    // --- reads -------------------------------------------------------------------------------------

    [Fact]
    public void Read_Word_Formats()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 100, new ushort[] { 0x5678, 0x1234, 0xFFFF });
        Assert.Equal("22136", Handle(device, "RD DM100").Response);
        Assert.Equal("22136", Handle(device, "RD DM100.U").Response);
        Assert.Equal("-1", Handle(device, "RD DM102.S").Response);
        Assert.Equal("305419896", Handle(device, "RD DM100.D").Response);  // 0x12345678, low word first
        Assert.Equal("-60876", Handle(device, "RD DM101.L").Response);    // 0xFFFF1234 as signed 32
        Assert.Equal("5678", Handle(device, "RD DM100.H").Response);
        Assert.Equal("FFFF", Handle(device, "RD DM102.H").Response);
        var o = Handle(device, "RD DM100");
        Assert.Equal(("read", "DM100", 1, null), (o.Op, o.Target, o.Points, o.Error));
    }

    [Fact]
    public void Read_Multiple_Words_And_Bits()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 100, new ushort[] { 1234, 2, 65535 });
        device.Seed(TableKind.Bits, 16, new[] { true, false, true, true });
        Assert.Equal("1234 2 65535", Handle(device, "RDS DM100 3").Response);
        Assert.Equal("2 -1", Handle(device, "RDS DM101.S 2").Response);
        Assert.Equal("1 0 1 1", Handle(device, "RDS R100 4").Response);
        Assert.Equal("1", Handle(device, "RD R100").Response);
        Assert.Equal("0", Handle(device, "RD R101").Response);
        var o = Handle(device, "RDS R100 4");
        Assert.Equal(("read", "R100", 4, null), (o.Op, o.Target, o.Points, o.Error));
    }

    [Fact]
    public void Read_DoubleWords_CountIsInValues()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 100, new ushort[] { 1, 0, 2, 0 });
        Assert.Equal("1 2", Handle(device, "RDS DM100.D 2").Response);
    }

    [Fact]
    public void Read_UserMaps()
    {
        var device = NewDevice();
        device.Seed(TableKind.ReadOnlyBits, 32, new[] { true });
        device.Seed(TableKind.ReadOnlyWords, 7, new ushort[] { 77 });
        device.Seed(TableKind.Words, 105, new ushort[] { 55 });
        Assert.Equal("1", Handle(device, "RD MR200", UserMaps(device)).Response);
        Assert.Equal("77", Handle(device, "RD EM7", UserMaps(device)).Response);
        Assert.Equal("55", Handle(device, "RD W5", UserMaps(device)).Response);
    }

    // --- writes ------------------------------------------------------------------------------------

    [Fact]
    public void Write_Word_Formats()
    {
        var device = NewDevice();
        Assert.Equal("OK", Handle(device, "WR DM100 5").Response);
        Assert.Equal(5, Word(device, 100));
        Assert.Equal("OK", Handle(device, "WR DM100.S -1").Response);
        Assert.Equal(0xFFFF, Word(device, 100));
        Assert.Equal("OK", Handle(device, "WR DM100.D 305419896").Response);
        Assert.Equal((0x5678, 0x1234), (Word(device, 100), Word(device, 101)));
        Assert.Equal("OK", Handle(device, "WR DM100.L -2").Response);
        Assert.Equal((0xFFFE, 0xFFFF), (Word(device, 100), Word(device, 101)));
        Assert.Equal("OK", Handle(device, "WR DM100.H abcd").Response);
        Assert.Equal(0xABCD, Word(device, 100));
        var o = Handle(device, "WRS DM100 3 1 2 3");
        Assert.Equal(("OK", "write", "DM100", 3, null), (o.Response, o.Op, o.Target, o.Points, o.Error));
        Assert.Equal((1, 2, 3), (Word(device, 100), Word(device, 101), Word(device, 102)));
        Assert.Equal("OK", Handle(device, "WRS DM100.D 2 65536 1").Response);
        Assert.Equal((0, 1, 1, 0), (Word(device, 100), Word(device, 101), Word(device, 102), Word(device, 103)));
    }

    [Fact]
    public void Write_Bits_And_SetReset()
    {
        var device = NewDevice();
        Assert.Equal("OK", Handle(device, "WR R100 1").Response);
        Assert.True(device.ReadBit(TableKind.Bits, 16));
        Assert.Equal("OK", Handle(device, "WRS R100 3 0 1 1").Response);
        Assert.Equal((false, true, true), (device.ReadBit(TableKind.Bits, 16), device.ReadBit(TableKind.Bits, 17), device.ReadBit(TableKind.Bits, 18)));
        Assert.Equal("OK", Handle(device, "ST R5").Response);
        Assert.True(device.ReadBit(TableKind.Bits, 5));
        Assert.Equal("OK", Handle(device, "RS R5").Response);
        Assert.False(device.ReadBit(TableKind.Bits, 5));
        var o = Handle(device, "ST R5");
        Assert.Equal(("set", "R5", 1), (o.Op, o.Target, o.Points));
    }

    // --- errors ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("RD X0", "E0", "unknown device")]
    [InlineData("RD R116", "E0", "bit 16")]
    [InlineData("RD DM100.X", "E0", "unknown format")]
    [InlineData("RD R100.U", "E0", "format on a relay")]
    [InlineData("RD MR0", "E0", "MR is not mapped by default")]
    [InlineData("RD DM256", "E0", "beyond the map (0..255)")]
    [InlineData("RD DM255.D", "E0", "double word runs past the map")]
    [InlineData("RDS DM254 3", "E0", "count runs past the map")]
    [InlineData("RDS R25500 20", "E0", "bits run past the map (bits 0..255)")]
    [InlineData("RD", "E1", "missing device")]
    [InlineData("RD DM100 1", "E1", "extra argument")]
    [InlineData("RDS DM100", "E1", "missing count")]
    [InlineData("RDS DM100 0", "E1", "zero count")]
    [InlineData("RDS DM100 1001", "E1", "count over 1000")]
    [InlineData("RDS DM100 x", "E1", "count not a number")]
    [InlineData("WR DM100", "E1", "missing value")]
    [InlineData("WR DM100 70000", "E1", "value over 16 bits")]
    [InlineData("WR DM100.S 40000", "E1", "value over signed 16 bits")]
    [InlineData("WR DM100.H 12345", "E1", "hex over 4 digits")]
    [InlineData("WR DM100 abc", "E1", "value not a number")]
    [InlineData("WR R100 2", "E1", "relay value not 0/1")]
    [InlineData("WRS DM100 3 1 2", "E1", "fewer values than count")]
    [InlineData("WRS DM100 1 1 2", "E1", "more values than count")]
    [InlineData("ST DM100", "E0", "set on a word device")]
    [InlineData("RS R116", "E0", "reset with bit 16")]
    [InlineData("XX DM100", "E1", "unknown command")]
    [InlineData("STS R100 2", "E1", "unsupported command")]
    public void Errors(string line, string error, string why)
    {
        var o = Handle(NewDevice(), line);
        Assert.True(error == o.Response && error == o.Error, $"{why}: expected {error}, got {o.Response}");
    }

    [Fact]
    public void Write_ToReadOnlyTable_IsRejected_E4()
    {
        var device = NewDevice();
        var o = Handle(device, "WR EM0 1", UserMaps(device));
        Assert.Equal(("E4", "E4"), (o.Response, o.Error));
        Assert.Equal(("E4", "E4"), (Handle(device, "ST MR0", UserMaps(device)).Response, Handle(device, "ST MR0", UserMaps(device)).Error));
        Assert.Equal(0, Word(device, 0));
    }

    [Fact]
    public void EmptyLine_HasNoResponse()
    {
        var o = Handle(NewDevice(), "");
        Assert.Null(o.Response);
    }

    [Fact]
    public void UserMap_OverridesDefault()
    {
        var device = NewDevice();
        var maps = KvMap.Merge(device, [new KvMap(Dev("DM"), TableKind.Words, 200, 10)]);
        Assert.Equal(2, maps.Length); // DM (overridden) + R (default)
        device.Seed(TableKind.Words, 203, new ushort[] { 9 });
        Assert.Equal("9", Handle(device, "RD DM3", maps).Response);
        Assert.Equal("E0", Handle(device, "RD DM10", maps).Response);
    }

    // --- runner (TCP) ------------------------------------------------------------------------------

    private static async Task ExpectLogAsync(CapturingProvider provider, Predicate<CapturingProvider.Captured> predicate)
    {
        for (int i = 0; i < 50 && !provider.Entries.Exists(predicate); i++)
        {
            await Task.Delay(20);
        }
        Assert.Contains(provider.Entries, predicate);
    }

    private static async Task<string> ExchangeAsync(NetworkStream stream, string line)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(line));
        var buf = new byte[1024];
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(3000);
        while (!sb.ToString().EndsWith("\r\n"))
        {
            int n = await stream.ReadAsync(buf, cts.Token);
            Assert.NotEqual(0, n);
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
        return sb.ToString();
    }

    [Fact]
    public async Task Tcp_Read_Write_Lines()
    {
        var plc = NewDevice();
        plc.Seed(TableKind.Words, 0, new ushort[] { 1111, 2222 });
        var provider = new CapturingProvider();
        var server = new KvServer("kv-srv", new IPEndPoint(IPAddress.Loopback, 0), plc, KvMap.Defaults(plc), provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();
        Assert.Equal("CC\r\n", await ExchangeAsync(stream, "CR\r"));
        Assert.Equal("1111 2222\r\n", await ExchangeAsync(stream, "RDS DM0 2\r"));
        Assert.Equal("OK\r\n", await ExchangeAsync(stream, "WR DM5 42\r\n"));   // CR LF terminated lines are accepted too
        Assert.Equal("42\r\n", await ExchangeAsync(stream, "RD DM5\r"));
        Assert.Equal("E0\r\n", await ExchangeAsync(stream, "RD MR0\r"));
        Assert.Equal(42, Word(plc, 5));

        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnAccepted.Id);
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("target=DM0") && e.Message.Contains("qty=2") && e.Message.Contains("result=ok"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=write") && e.Message.Contains("target=DM5"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id && e.Message.Contains("err=E0"));
        cts.Cancel();
    }

    [Fact]
    public async Task Tcp_OverlongLine_ClosesConnection_LogsMalformed()
    {
        var plc = NewDevice();
        var provider = new CapturingProvider();
        var server = new KvServer("kv-srv", new IPEndPoint(IPAddress.Loopback, 0), plc, KvMap.Defaults(plc), provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('A', KvHostLink.LineMax + 1)));
        var buf = new byte[16];
        using var readCts = new CancellationTokenSource(3000);
        try
        {
            Assert.Equal(0, await stream.ReadAsync(buf, readCts.Token));
        }
        catch (IOException)
        {
        }
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.MalformedFrame.Id);
        cts.Cancel();
    }
}
