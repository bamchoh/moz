using System.Net;
using System.Net.Sockets;
using Moz.Devices;
using Moz.Fins;

namespace Moz.Tests;

/// <summary>
/// Omron FINS server (phase 19): the area table, the pure frame handler
/// against a device (default map DM → holding registers, user maps for the rest), the
/// FINS/TCP framing with its node address exchange, and the TCP / UDP
/// runners end to end with hand-written clients.
/// </summary>
public class FinsServerTests
{
    // ICF 80 (command, response required), RSV, GCT 02, DNA 0 / DA1 10 / DA2 0, SNA 0 / SA1 11 / SA2 0, SID 1
    private const string Header = "800002000A00000B0001";
    private const string ResponseHeader = "C00002000B00000A0001";

    private static Device NewDevice() => new("plc", coils: 256, discreteInputs: 256, holdingRegisters: 256, inputRegisters: 256);

    /// <summary>CIO 0..127 → holding 128..255 (CIO100 ↔ holding[228]), E0 → input 0..255, plus the default DM.</summary>
    private static FinsMap[] UserMaps(Device device) => FinsMap.Merge(device,
    [
        new FinsMap(Area("CIO"), TableKind.HoldingRegister, 128, 128),
        new FinsMap(Area("E0"), TableKind.InputRegister, 0, 256),
    ]);

    private static ushort Word(Device device, TableKind table, int index)
    {
        var one = new ushort[1];
        device.Read(table, index, one);
        return one[0];
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

    private static FinsArea Area(string code)
    {
        Assert.True(FinsArea.TryFromCode(code, out var area), code);
        return area;
    }

    private static FinsOutcome Handle(Device device, string frameHex, IReadOnlyList<FinsMap>? maps = null)
    {
        var frame = Convert.FromHexString(frameHex);
        Assert.True(FinsServer.TryHandle(frame, device, maps ?? FinsMap.Defaults(device), out var outcome));
        return outcome;
    }

    // --- area table ----------------------------------------------------------------------------

    [Theory]
    [InlineData("DM", 0x82, 0x02)]
    [InlineData("cio", 0xB0, 0x30)]
    [InlineData("W", 0xB1, 0x31)]
    [InlineData("H", 0xB2, 0x32)]
    [InlineData("A", 0xB3, 0x33)]
    [InlineData("E0", 0xA0, 0x20)]
    [InlineData("e3", 0xA3, 0x23)]
    [InlineData("E18", 0xEB, 0x6B)]
    [InlineData("ED", 0xE0, 0x60)]
    [InlineData("EC", 0xAC, 0x2C)]
    public void Area_FromCode_And_Binary(string code, int word, int bit)
    {
        Assert.True(FinsArea.TryFromCode(code, out var area));
        Assert.Equal((word, bit), (area.WordCode, area.BitCode));
        Assert.Equal(code.ToUpperInvariant(), area.Code);
        Assert.True(FinsArea.TryFromBinary((byte)word, out var byWord, out var isBit));
        Assert.Equal((area, false), (byWord, isBit));
        Assert.True(FinsArea.TryFromBinary((byte)bit, out var byBit, out isBit));
        Assert.Equal((area, true), (byBit, isBit));
    }

    [Theory]
    [InlineData("T")]
    [InlineData("E19")]
    [InlineData("E")]
    [InlineData("DM1")]
    [InlineData("")]
    public void Area_Unknown_IsRejected(string code)
    {
        Assert.False(FinsArea.TryFromCode(code, out _));
    }

    [Fact]
    public void Area_UnknownBinary_IsRejected()
    {
        Assert.False(FinsArea.TryFromBinary(0xFF, out _, out _));
        Assert.False(FinsArea.TryFromBinary(0x89, out _, out _)); // timer/counter PV: not served
        Assert.False(FinsArea.TryFromBinary(0xB9, out _, out _)); // E19 word: beyond the last bank
    }

    [Fact]
    public void Area_Format()
    {
        Assert.Equal("DM100", Area("DM").Format(100, 0, false));
        Assert.Equal("CIO100.05", Area("CIO").Format(100, 5, true));
        Assert.Equal("E0_7", Area("E0").Format(7, 0, false));
    }

    // --- handler: reads and writes --------------------------------------------------------------

    [Fact]
    public void Read_Words_FromDefaultMap()
    {
        var device = NewDevice();
        device.Seed(TableKind.HoldingRegister, 100, new ushort[] { 0x1234, 0x0002, 0xFFFF });
        var o = Handle(device, Header + "0101" + "82" + "0064" + "00" + "0003");
        Assert.Equal(ResponseHeader + "0101" + "0000" + "12340002FFFF", Convert.ToHexString(o.Response));
        Assert.Equal(("read", "DM100", 3, 0, true), (o.Op, o.Target, o.Points, (int)o.EndCode, o.ResponseRequired));
    }

    [Fact]
    public void Read_Bits_OfMappedWordArea()
    {
        var device = NewDevice();
        device.Seed(TableKind.HoldingRegister, 228, new ushort[] { 0x8031, 0x0001 });
        // CIO 100.04 .. 100.06 → coil 4, 5, 6 of holding[228]
        var o = Handle(device, Header + "0101" + "30" + "0064" + "04" + "0003", UserMaps(device));
        Assert.Equal(ResponseHeader + "0101" + "0000" + "010100", Convert.ToHexString(o.Response));
        Assert.Equal(("read", "CIO100.04", 3), (o.Op, o.Target, o.Points));
        // CIO 100.14 .. 101.01 crosses the word boundary
        o = Handle(device, Header + "0101" + "30" + "0064" + "0E" + "0004", UserMaps(device));
        Assert.Equal(ResponseHeader + "0101" + "0000" + "00010100", Convert.ToHexString(o.Response));
    }

    [Fact]
    public void Write_Words_UpdatesDevice()
    {
        var device = NewDevice();
        var o = Handle(device, Header + "0102" + "82" + "0064" + "00" + "0002" + "12345678");
        Assert.Equal(ResponseHeader + "0102" + "0000", Convert.ToHexString(o.Response));
        Assert.Equal(("write", "DM100", 2, 0), (o.Op, o.Target, o.Points, (int)o.EndCode));
        var w = new ushort[2];
        device.Read(TableKind.HoldingRegister, 100, w);
        Assert.Equal([0x1234, 0x5678], w);
    }

    [Fact]
    public void Write_Bits_ReadModifyWrites_TheWords()
    {
        var device = NewDevice();
        device.Seed(TableKind.HoldingRegister, 228, new ushort[] { 0x8031, 0x0001 });
        // CIO 100.14 = 1, 100.15 = 0, 101.00 = 0, 101.01 = 1
        var o = Handle(device, Header + "0102" + "30" + "0064" + "0E" + "0004" + "01000001", UserMaps(device));
        Assert.Equal(0, o.EndCode);
        Assert.Equal(("write", "CIO100.14", 4), (o.Op, o.Target, o.Points));
        var w = new ushort[2];
        device.Read(TableKind.HoldingRegister, 228, w);
        Assert.Equal([0x4031, 0x0002], w);
    }

    [Fact]
    public void MultipleRead_MixesAreasAndUnits()
    {
        var device = NewDevice();
        device.Seed(TableKind.HoldingRegister, 100, new ushort[] { 0x1234 });
        device.Seed(TableKind.HoldingRegister, 228, new ushort[] { 0x0010 });
        device.Seed(TableKind.InputRegister, 5, new ushort[] { 0xABCD });
        var o = Handle(device, Header + "0104" + "82006400" + "30006404" + "A0000500", UserMaps(device));
        Assert.Equal(ResponseHeader + "0104" + "0000" + "82" + "1234" + "30" + "01" + "A0" + "ABCD", Convert.ToHexString(o.Response));
        Assert.Equal(("read-multi", "DM100", 3, 0), (o.Op, o.Target, o.Points, (int)o.EndCode));
    }

    [Fact]
    public void Header_IsSwapped_And_NoResponseFlag_IsReported()
    {
        var device = NewDevice();
        // ICF 81: response not required; GCT 07 and SID 5A are echoed; nodes swap
        var o = Handle(device, "8100070122030344045A" + "0101" + "82" + "0000" + "00" + "0001");
        Assert.Equal("C100070344040122035A" + "0101" + "0000" + "0000", Convert.ToHexString(o.Response));
        Assert.False(o.ResponseRequired);
    }

    [Theory]
    [InlineData("0105820064000003", 0x0401, "unknown command 0105")]
    [InlineData("0101B00064000003", 0x1101, "CIO is not mapped by default")]
    [InlineData("0101FF0064000003", 0x1101, "unknown area code")]
    [InlineData("0101020064100003", 0x1103, "bit 16 on a bit code")]
    [InlineData("0101820064010003", 0x1103, "bit designation on a word code")]
    [InlineData("0101820100000003", 0x1103, "start address 256 beyond the map (0..255)")]
    [InlineData("01018200FE000003", 0x1104, "254 + 3 words beyond the map")]
    [InlineData("01010200FF0F0002", 0x1104, "DM255.15 + 2 bits beyond the map")]
    [InlineData("0101820064000000", 0x110C, "zero items")]
    [InlineData("01018200000003E8", 0x110B, "1000 words would not fit a response")]
    [InlineData("0102820064000002" + "1234", 0x1003, "write data shorter than the item count")]
    [InlineData("0102820064000001" + "123456", 0x1003, "write data longer than the item count")]
    [InlineData("010182006400", 0x1002, "parameters cut short")]
    [InlineData("0101820064000003" + "00", 0x1001, "trailing bytes on a read")]
    [InlineData("0104", 0x110C, "multiple read with no items")]
    [InlineData("0104" + "820064", 0x1003, "multiple read item cut short")]
    [InlineData("0104" + "82006400" + "B0006400", 0x1101, "multiple read with an unmapped item")]
    public void Errors_EndCodes(string bodyHex, int endCode, string why)
    {
        var device = NewDevice();
        var o = Handle(device, Header + bodyHex);
        Assert.True(endCode == o.EndCode, $"{why}: expected {endCode:X4}, got {o.EndCode:X4}");
        // error response: header + command echo + end code, no data
        Assert.Equal(ResponseHeader + bodyHex[..4] + $"{endCode:X4}", Convert.ToHexString(o.Response));
    }

    [Fact]
    public void Write_ToReadOnlyTable_IsRejected_2101()
    {
        var device = NewDevice();
        var o = Handle(device, Header + "0102" + "A0" + "0005" + "00" + "0001" + "1234", UserMaps(device));
        Assert.Equal(0x2101, o.EndCode);
        Assert.Equal(0, Word(device, TableKind.InputRegister, 5));
    }

    [Fact]
    public void Write_TooManyItems_IsRejected_1001()
    {
        var device = NewDevice();
        var o = Handle(device, Header + "0102" + "82" + "0000" + "00" + "03E8" + new string('0', 1000 * 4));
        Assert.Equal(0x1001, o.EndCode);
    }

    [Fact]
    public void MultipleRead_TooManyItems_IsRejected_110B()
    {
        var device = NewDevice();
        var items = string.Concat(Enumerable.Repeat("82000000", 129));
        var o = Handle(device, Header + "0104" + items);
        Assert.Equal(0x110B, o.EndCode);
        o = Handle(device, Header + "0104" + items[..(128 * 8)]);
        Assert.Equal(0, o.EndCode);
    }

    [Fact]
    public void Malformed_Frames_AreNotHandled()
    {
        var device = NewDevice();
        var maps = FinsMap.Defaults(device);
        Assert.False(FinsServer.TryHandle(Convert.FromHexString("800002000A00000B00"), device, maps, out _));      // header cut short
        Assert.False(FinsServer.TryHandle(Convert.FromHexString("800002000A00000B0001" + "01"), device, maps, out _)); // no command
        Assert.False(FinsServer.TryHandle(Convert.FromHexString("C00002000A00000B0001" + "0101" + "0000"), device, maps, out _)); // a response, not a command
    }

    [Fact]
    public void UserMap_OverridesDefault_And_AddsAreas()
    {
        var device = NewDevice();
        var maps = FinsMap.Merge(device, [new FinsMap(Area("DM"), TableKind.HoldingRegister, 200, 10)]);
        Assert.Single(maps);
        device.Seed(TableKind.HoldingRegister, 203, new ushort[] { 0x0A0B });
        var o = Handle(device, Header + "0101" + "82" + "0003" + "00" + "0001", maps);
        Assert.Equal(ResponseHeader + "0101" + "0000" + "0A0B", Convert.ToHexString(o.Response));
        o = Handle(device, Header + "0101" + "82" + "000A" + "00" + "0001", maps);
        Assert.Equal(0x1103, o.EndCode);
    }

    // --- FINS/TCP framing ---------------------------------------------------------------------

    [Fact]
    public void Tcp_Header_Parses_And_Rejects()
    {
        Assert.True(FinsTcp.TryParseHeader(Convert.FromHexString("46494E53" + "0000000C" + "00000000" + "00000000"), out var command, out var error, out var payload));
        Assert.Equal((0u, 0u, 4), (command, error, payload));
        Assert.True(FinsTcp.TryParseHeader(Convert.FromHexString("46494E53" + "00000018" + "00000002" + "00000000"), out command, out _, out payload));
        Assert.Equal((2u, 16), (command, payload));
        Assert.False(FinsTcp.TryParseHeader(Convert.FromHexString("46494E54" + "0000000C" + "00000000" + "00000000"), out _, out _, out _)); // not "FINS"
        Assert.False(FinsTcp.TryParseHeader(Convert.FromHexString("46494E53" + "00000007" + "00000000" + "00000000"), out _, out _, out _)); // shorter than the fixed part
        Assert.False(FinsTcp.TryParseHeader(Convert.FromHexString("46494E53" + "00001000" + "00000002" + "00000000"), out _, out _, out _)); // over the frame limit
        Assert.False(FinsTcp.TryParseHeader(Convert.FromHexString("46494E53" + "0000000C" + "000000"), out _, out _, out _));
    }

    [Fact]
    public void Tcp_Build_And_NodeConfirm()
    {
        Assert.Equal("46494E53" + "0000000A" + "00000002" + "00000000" + "0102", Convert.ToHexString(FinsTcp.Build(FinsTcp.CommandFrame, 0, Convert.FromHexString("0102"))));
        Assert.Equal("46494E53" + "00000010" + "00000001" + "00000000" + "00000005" + "0000000A", Convert.ToHexString(FinsTcp.NodeAddressConfirm(5, 10)));
        Assert.Equal("46494E53" + "00000008" + "00000003" + "00000003", Convert.ToHexString(FinsTcp.Build(FinsTcp.CommandError, FinsTcp.ErrorUnsupportedCommand, [])));
    }

    [Theory]
    [InlineData(0, "192.168.0.1", 10, 1)]      // auto: last octet of the client address
    [InlineData(0, "192.168.0.10", 10, 1)]     // ... unless it is the server's own node
    [InlineData(0, "192.168.0.1", 1, 2)]
    [InlineData(0, "192.168.0.0", 10, 1)]      // 0 is not a node address
    [InlineData(0, "10.0.0.255", 10, 1)]       // nor is 255
    [InlineData(7, "192.168.0.1", 10, 7)]      // explicit request is honoured
    public void Tcp_AssignsClientNode(int requested, string remote, int serverNode, int expected)
    {
        Assert.Equal((byte)expected, FinsTcp.AssignClientNode((byte)requested, IPAddress.Parse(remote), (byte)serverNode));
    }

    // --- runners ----------------------------------------------------------------------------------

    private static async Task<FinsTcpServer> StartTcpAsync(Device plc, CapturingProvider provider, CancellationToken ct, FinsMap[]? maps = null)
    {
        var server = new FinsTcpServer("fins-srv", new IPEndPoint(IPAddress.Loopback, 0), 10, plc, maps ?? FinsMap.Defaults(plc), provider.CreateLogger("srv"));
        _ = server.RunAsync(ct);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        return server;
    }

    private static async Task<byte[]> ExchangeAsync(NetworkStream stream, byte[] request)
    {
        await stream.WriteAsync(request);
        var header = new byte[FinsTcp.HeaderSize];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(FinsTcp.TryParseHeader(header, out _, out _, out var payloadLength));
        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        return [.. header, .. payload];
    }

    [Fact]
    public async Task Tcp_Handshake_Read_Write()
    {
        var plc = NewDevice();
        plc.Seed(TableKind.HoldingRegister, 0, new ushort[] { 0x1111, 0x2222 });
        var provider = new CapturingProvider();
        using var cts = new CancellationTokenSource();
        var server = await StartTcpAsync(plc, provider, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();

        // node address exchange: client asks for automatic assignment, server answers client 1 / server 10
        var confirm = await ExchangeAsync(stream, Convert.FromHexString("46494E53" + "0000000C" + "00000000" + "00000000" + "00000000"));
        Assert.Equal("46494E53" + "00000010" + "00000001" + "00000000" + "00000001" + "0000000A", Convert.ToHexString(confirm));

        // read DM0..1
        var read = await ExchangeAsync(stream, FinsTcp.Build(FinsTcp.CommandFrame, 0, Convert.FromHexString("80000200" + "0A00" + "0001" + "00" + "01" + "0101" + "82" + "0000" + "00" + "0002")));
        Assert.Equal("46494E53" + "0000001A" + "00000002" + "00000000" + "C0000200" + "0100" + "000A" + "00" + "01" + "0101" + "0000" + "11112222", Convert.ToHexString(read));

        // write DM5
        var write = await ExchangeAsync(stream, FinsTcp.Build(FinsTcp.CommandFrame, 0, Convert.FromHexString("80000200" + "0A00" + "0001" + "00" + "02" + "0102" + "82" + "0005" + "00" + "0001" + "BEEF")));
        Assert.EndsWith("0102" + "0000", Convert.ToHexString(write));
        Assert.Equal(0xBEEF, Word(plc, TableKind.HoldingRegister, 5));

        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnAccepted.Id);
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("target=DM0") && e.Message.Contains("qty=2") && e.Message.Contains("result=ok"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=write") && e.Message.Contains("target=DM5"));
        cts.Cancel();
    }

    [Fact]
    public async Task Tcp_FrameWithoutHandshake_IsServed_And_ErrorsAreReported()
    {
        var plc = NewDevice();
        var provider = new CapturingProvider();
        using var cts = new CancellationTokenSource();
        var server = await StartTcpAsync(plc, provider, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();
        // no node exchange: tolerated. Read of an unmapped area → end code 1101 inside a normal frame
        var resp = await ExchangeAsync(stream, FinsTcp.Build(FinsTcp.CommandFrame, 0, Convert.FromHexString(Header + "0101" + "B0" + "0000" + "00" + "0001")));
        Assert.EndsWith(ResponseHeader + "0101" + "1101", Convert.ToHexString(resp));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id && e.Message.Contains("err=1101"));

        // unknown FINS/TCP command → error frame (command 3, code 3), then the connection is closed
        var err = await ExchangeAsync(stream, FinsTcp.Build(5, 0, []));
        Assert.Equal("46494E53" + "00000008" + "00000003" + "00000003", Convert.ToHexString(err));
        var buf = new byte[16];
        using var readCts = new CancellationTokenSource(2000);
        try
        {
            Assert.Equal(0, await stream.ReadAsync(buf, readCts.Token));
        }
        catch (IOException)
        {
            // reset: also closed
        }
        cts.Cancel();
    }

    [Fact]
    public async Task Tcp_BadMagic_ClosesConnection_LogsMalformed()
    {
        var plc = NewDevice();
        var provider = new CapturingProvider();
        using var cts = new CancellationTokenSource();
        var server = await StartTcpAsync(plc, provider, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString("500000FFFF03000C00100001040000640000A80300")); // an MC frame
        var buf = new byte[32];
        using var readCts = new CancellationTokenSource(2000);
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

    [Fact]
    public async Task Udp_Read_Write_NoResponseFlag_And_Malformed()
    {
        var plc = NewDevice();
        plc.Seed(TableKind.HoldingRegister, 0, new ushort[] { 0x1111, 0x2222 });
        var provider = new CapturingProvider();
        var server = new FinsUdpServer("fins-udp", new IPEndPoint(IPAddress.Loopback, 0), plc, FinsMap.Defaults(plc), provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Client.ReceiveTimeout = 2000;
        var target = server.BoundEndpoint!;

        await client.SendAsync(Convert.FromHexString(Header + "0101" + "82" + "0000" + "00" + "0002"), target);
        var resp = await client.ReceiveAsync(new CancellationTokenSource(2000).Token);
        Assert.Equal(ResponseHeader + "0101" + "0000" + "11112222", Convert.ToHexString(resp.Buffer));

        // ICF 81: write without a response
        await client.SendAsync(Convert.FromHexString("810002000A00000B0002" + "0102" + "82" + "0005" + "00" + "0001" + "BEEF"), target);
        // a malformed datagram (too short) is dropped
        await client.SendAsync(Convert.FromHexString("8000"), target);
        // the next read is answered — proving neither of the two above produced a response
        await client.SendAsync(Convert.FromHexString(Header + "0101" + "82" + "0005" + "00" + "0001"), target);
        resp = await client.ReceiveAsync(new CancellationTokenSource(2000).Token);
        Assert.Equal(ResponseHeader + "0101" + "0000" + "BEEF", Convert.ToHexString(resp.Buffer));
        Assert.Equal(0xBEEF, Word(plc, TableKind.HoldingRegister, 5));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.MalformedFrame.Id);
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id && e.Message.Contains("op=write") && e.Message.Contains("target=DM5"));
        cts.Cancel();
    }
}
