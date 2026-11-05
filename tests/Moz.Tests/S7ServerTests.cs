using System.Net;
using System.Net.Sockets;
using System.Text;
using Moz.Devices;
using Moz.S7;

namespace Moz.Tests;

/// <summary>
/// Siemens S7 server (phase 22): the pure S7comm PDU handler against a
/// device (default map DB1 → words, user maps for M / I / Q / other DBs),
/// byte- and bit-granular access on top of big-endian words, minimal SZL,
/// ISO-on-TCP framing and the TCP runner with a hand-written client.
/// </summary>
public class S7ServerTests
{
    private static Device NewDevice() => new("plc", bits: 16, readOnlyBits: 16, words: 256, readOnlyWords: 256);

    /// <summary>M → words 100..163 (128 bytes), I → readOnlyWords 0..63, Q → words 200..231, DB5 → words 0..9, plus the default DB1.</summary>
    private static S7Map[] UserMaps(Device device) => S7Map.Merge(device,
    [
        new S7Map(Area("M"), TableKind.Words, 100, 64),
        new S7Map(Area("I"), TableKind.ReadOnlyWords, 0, 64),
        new S7Map(Area("Q"), TableKind.Words, 200, 32),
        new S7Map(Area("DB5"), TableKind.Words, 0, 10),
    ]);

    private static S7Area Area(string code)
    {
        Assert.True(S7Area.TryFromCode(code, out var area), code);
        return area;
    }

    private static ushort Word(Device device, int index, TableKind table = TableKind.Words)
    {
        var one = new ushort[1];
        device.Read(table, index, one);
        return one[0];
    }

    private static S7Outcome Handle(Device device, string pduHex, IReadOnlyList<S7Map>? maps = null, S7Session? session = null)
    {
        Assert.True(S7Server.TryHandle(Convert.FromHexString(pduHex), device, maps ?? S7Map.Defaults(device), session ?? new S7Session(), out var outcome));
        return outcome;
    }

    // job header: 32 01 | reserved 0000 | pdu ref 0100 | param length | data length
    private static string Job(string paramHex, string dataHex = "") =>
        "32010000" + "0100" + $"{paramHex.Length / 2:X4}" + $"{dataHex.Length / 2:X4}" + paramHex + dataHex;

    // ack_data header: 32 03 | 0000 | 0100 | param length | data length | error class/code 0000
    private static string AckData(string paramHex, string dataHex = "", string error = "0000") =>
        "32030000" + "0100" + $"{paramHex.Length / 2:X4}" + $"{dataHex.Length / 2:X4}" + error + paramHex + dataHex;

    /// <summary>One S7ANY item: transport size, count, DB number, area, byte address and bit.</summary>
    private static string Item(int transport, int count, int db, int area, int byteAddress, int bit = 0) =>
        "120A10" + $"{transport:X2}{count:X4}{db:X4}{area:X2}{(byteAddress * 8 + bit):X6}";

    // --- areas and maps -------------------------------------------------------------------------------

    [Theory]
    [InlineData("DB1", 0x84, 1)]
    [InlineData("db12", 0x84, 12)]
    [InlineData("M", 0x83, 0)]
    [InlineData("I", 0x81, 0)]
    [InlineData("Q", 0x82, 0)]
    [InlineData("e", 0x81, 0)]   // German spellings
    [InlineData("A", 0x82, 0)]
    public void Area_FromCode(string code, int areaCode, int db)
    {
        Assert.True(S7Area.TryFromCode(code, out var area));
        Assert.Equal((areaCode, db), (area.Code, area.Db));
        Assert.True(S7Area.TryFromWire((byte)areaCode, db, out var fromWire));
        Assert.Equal(area, fromWire);
    }

    [Theory]
    [InlineData("DB")]
    [InlineData("DB0")]
    [InlineData("DB65536")]
    [InlineData("T")]
    [InlineData("X")]
    [InlineData("")]
    public void Area_Unknown_IsRejected(string code)
    {
        Assert.False(S7Area.TryFromCode(code, out _));
    }

    [Fact]
    public void Area_Names()
    {
        Assert.Equal("DB1", Area("DB1").Name);
        Assert.Equal("M", Area("m").Name);
        Assert.Equal("DB1.DBW10", Area("DB1").Format(10, 0, 2));
        Assert.Equal("DB1.DBX10.3", Area("DB1").Format(10, 3, 0));
        Assert.Equal("DB1.DBB11", Area("DB1").Format(11, 0, 1));
        Assert.Equal("DB1.DBD4", Area("DB1").Format(4, 0, 4));
        Assert.Equal("MB5", Area("M").Format(5, 0, 1));
        Assert.Equal("I0.0", Area("I").Format(0, 0, 0));
        Assert.Equal("QW2", Area("Q").Format(2, 0, 2));
        Assert.Equal("MD8", Area("M").Format(8, 0, 4));
    }

    [Fact]
    public void DefaultMap_IsDb1_OverWholeWordTable()
    {
        var device = NewDevice();
        var maps = S7Map.Defaults(device);
        Assert.Single(maps);
        Assert.Equal((Area("DB1"), TableKind.Words, 0, 256), (maps[0].Area, maps[0].Table, maps[0].Offset, maps[0].Count));
        var merged = S7Map.Merge(device, [new S7Map(Area("DB1"), TableKind.Words, 10, 5)]);
        Assert.Single(merged);
        Assert.Equal(10, merged[0].Offset);
    }

    // --- setup communication ---------------------------------------------------------------------------

    [Fact]
    public void SetupCommunication_NegotiatesPdu()
    {
        var device = NewDevice();
        var session = new S7Session();
        var o = Handle(device, Job("F000" + "0001" + "0001" + "0F00"), session: session);
        Assert.Equal(AckData("F000" + "0001" + "0001" + "03C0"), Convert.ToHexString(o.Response));
        Assert.Equal((960, "setup", "pdu=960", null), (session.PduLength, o.Op, o.Target, o.Error));
        o = Handle(device, Job("F000" + "0001" + "0001" + "00F0"), session: session);
        Assert.EndsWith("00F0", Convert.ToHexString(o.Response));
        Assert.Equal(240, session.PduLength);
    }

    // --- reads ------------------------------------------------------------------------------------------

    [Fact]
    public void Read_Word_Byte_OddByte_DWord()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 5, new ushort[] { 0x1234, 0x5678 });
        // DB1.DBW10 (bytes 10..11 = words[5])
        var o = Handle(device, Job("0401" + Item(4, 1, 1, 0x84, 10)));
        Assert.Equal(AckData("0401", "FF04" + "0010" + "1234"), Convert.ToHexString(o.Response));
        Assert.Equal(("read", "DB1.DBW10", 1, null), (o.Op, o.Target, o.Points, o.Error));
        // DB1.DBB11, 2 bytes: the low byte of words[5] and the high byte of words[6]
        o = Handle(device, Job("0401" + Item(2, 2, 1, 0x84, 11)));
        Assert.Equal(AckData("0401", "FF04" + "0010" + "3456"), Convert.ToHexString(o.Response));
        Assert.Equal("DB1.DBB11", o.Target);
        // DB1.DBD10 (4 bytes), REAL and DINT transports read the same bytes
        Assert.EndsWith("FF04" + "0020" + "12345678", Convert.ToHexString(Handle(device, Job("0401" + Item(6, 1, 1, 0x84, 10))).Response));
        Assert.EndsWith("FF04" + "0020" + "12345678", Convert.ToHexString(Handle(device, Job("0401" + Item(8, 1, 1, 0x84, 10))).Response));
        // 3 INT from byte 10: 6 bytes, the last word is still zero
        Assert.EndsWith("FF04" + "0030" + "123456780000", Convert.ToHexString(Handle(device, Job("0401" + Item(5, 3, 1, 0x84, 10))).Response));
    }

    [Fact]
    public void Read_Bits()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 5, new ushort[] { 0x0801 }); // byte 10 = 0x08 (bit 3), byte 11 = 0x01 (bit 0)
        var o = Handle(device, Job("0401" + Item(1, 1, 1, 0x84, 10, 3)));
        Assert.Equal(AckData("0401", "FF03" + "0001" + "01"), Convert.ToHexString(o.Response));
        Assert.Equal("DB1.DBX10.3", o.Target);
        Assert.EndsWith("FF03" + "0001" + "00", Convert.ToHexString(Handle(device, Job("0401" + Item(1, 1, 1, 0x84, 10, 2))).Response));
        Assert.EndsWith("FF03" + "0001" + "01", Convert.ToHexString(Handle(device, Job("0401" + Item(1, 1, 1, 0x84, 11, 0))).Response));
        // two bits from 10.7 cross into byte 11: 0, 1
        Assert.EndsWith("FF03" + "0002" + "0001", Convert.ToHexString(Handle(device, Job("0401" + Item(1, 2, 1, 0x84, 10, 7))).Response));
    }

    [Fact]
    public void Read_MultipleItems_WithPadding_AndItemErrors()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 0, new ushort[] { 0xAA00 });
        device.Seed(TableKind.Words, 100, new ushort[] { 0x0102 });
        device.Seed(TableKind.ReadOnlyWords, 1, new ushort[] { 0xBEEF });
        // DB1.DBB0 (1 byte, padded), MW0, IW2, DB2 (unmapped), DB1.DBW600 (out of range), timer (unsupported)
        var o = Handle(device, Job("0406" + Item(2, 1, 1, 0x84, 0) + Item(4, 1, 0, 0x83, 0) + Item(4, 1, 0, 0x81, 2)
            + Item(4, 1, 2, 0x84, 0) + Item(4, 1, 1, 0x84, 600) + Item(0x1D, 1, 0, 0x1D, 0)), UserMaps(device));
        Assert.Equal(AckData("0406",
            "FF04" + "0008" + "AA" + "00" +   // padded to an even length because another item follows
            "FF04" + "0010" + "0102" +
            "FF04" + "0010" + "BEEF" +
            "0A00" + "0000" +                 // object does not exist
            "0500" + "0000" +                 // address out of range
            "0600" + "0000"),                 // data type not supported
            Convert.ToHexString(o.Response));
        Assert.Equal(("read", "DB1.DBB0", 6, "0A"), (o.Op, o.Target, o.Points, o.Error));
    }

    [Fact]
    public void Read_Range_Checks_UseBytes()
    {
        var device = NewDevice(); // DB1 = 256 words = 512 bytes
        Assert.EndsWith("FF04" + "0010" + "0000", Convert.ToHexString(Handle(device, Job("0401" + Item(4, 1, 1, 0x84, 510))).Response));
        Assert.EndsWith("0500" + "0000", Convert.ToHexString(Handle(device, Job("0401" + Item(4, 1, 1, 0x84, 511))).Response));
        Assert.EndsWith("FF04" + "0008" + "00", Convert.ToHexString(Handle(device, Job("0401" + Item(2, 1, 1, 0x84, 511))).Response));
        Assert.EndsWith("0500" + "0000", Convert.ToHexString(Handle(device, Job("0401" + Item(1, 1, 1, 0x84, 512, 0))).Response));
        Assert.EndsWith("0500" + "0000", Convert.ToHexString(Handle(device, Job("0401" + Item(2, 0, 1, 0x84, 0))).Response)); // zero count
    }

    [Fact]
    public void Read_ResponseLargerThanPdu_IsRejected()
    {
        var device = NewDevice();
        var session = new S7Session(); // 240 by default: 240 - 12 (header) - 2 (param) - 4 (item header) = 222 bytes of data
        Assert.EndsWith("FF04" + "06F0" + new string('0', 222 * 2), Convert.ToHexString(Handle(device, Job("0401" + Item(2, 222, 1, 0x84, 0)), session: session).Response));
        var o = Handle(device, Job("0401" + Item(2, 223, 1, 0x84, 0)), session: session);
        Assert.Equal(AckData("04", "", "8500"), Convert.ToHexString(o.Response));
        Assert.Equal("8500", o.Error);
    }

    // --- writes -----------------------------------------------------------------------------------------

    [Fact]
    public void Write_Word_OddByte_Bit()
    {
        var device = NewDevice();
        device.Seed(TableKind.Words, 5, new ushort[] { 0x1234, 0x5678 });
        var o = Handle(device, Job("0501" + Item(4, 1, 1, 0x84, 10), "0004" + "0010" + "ABCD"));
        Assert.Equal(AckData("0501", "FF"), Convert.ToHexString(o.Response));
        Assert.Equal(("write", "DB1.DBW10", 1, null), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal(0xABCD, Word(device, 5));
        // DBB11 = 0x99: read-modify-write of words[5]
        o = Handle(device, Job("0501" + Item(2, 1, 1, 0x84, 11), "0004" + "0008" + "99"));
        Assert.Equal(AckData("0501", "FF"), Convert.ToHexString(o.Response));
        Assert.Equal((0xAB99, 0x5678), (Word(device, 5), Word(device, 6)));
        // DBB11 2 bytes = 0x11 0x22: spans words[5] (low byte) and words[6] (high byte)
        Handle(device, Job("0501" + Item(2, 2, 1, 0x84, 11), "0004" + "0010" + "1122"));
        Assert.Equal((0xAB11, 0x2278), (Word(device, 5), Word(device, 6)));
        // DBX10.3 = 1 (BIT transport 03, length in bits)
        // byte 10 is 0xAB = 1010 1011: bit 3 is set, bit 2 is clear
        o = Handle(device, Job("0501" + Item(1, 1, 1, 0x84, 10, 2), "0003" + "0001" + "01"));
        Assert.Equal(AckData("0501", "FF"), Convert.ToHexString(o.Response));
        Assert.Equal(0xAF11, Word(device, 5));
        Assert.Equal("DB1.DBX10.2", o.Target);
        Handle(device, Job("0501" + Item(1, 1, 1, 0x84, 10, 3), "0003" + "0001" + "00"));
        Assert.Equal(0xA711, Word(device, 5));
    }

    [Fact]
    public void Write_MultipleItems_WithFillBytes_AndItemErrors()
    {
        var device = NewDevice();
        // DBB0 = 0xAA (odd length → fill byte), MW0 = 0x0102, IW0 (read-only → 03), DB2 (→ 0A), DBW600 (→ 05), length mismatch (→ 07)
        var o = Handle(device, Job("0506" + Item(2, 1, 1, 0x84, 0) + Item(4, 1, 0, 0x83, 0) + Item(4, 1, 0, 0x81, 0)
            + Item(4, 1, 2, 0x84, 0) + Item(4, 1, 1, 0x84, 600) + Item(4, 1, 1, 0x84, 2),
            "0004" + "0008" + "AA" + "00" + "0004" + "0010" + "0102" + "0004" + "0010" + "0000"
            + "0004" + "0010" + "0000" + "0004" + "0010" + "0000" + "0004" + "0008" + "77"), UserMaps(device));
        Assert.Equal(AckData("0506", "FF" + "FF" + "03" + "0A" + "05" + "07"), Convert.ToHexString(o.Response));
        Assert.Equal(("write", "DB1.DBB0", 6, "03"), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal((0xAA00, 0x0102, 0), (Word(device, 0), Word(device, 100), Word(device, 0, TableKind.ReadOnlyWords)));
    }

    [Fact]
    public void Write_OctetString_Transport_IsAccepted()
    {
        var device = NewDevice();
        var o = Handle(device, Job("0501" + Item(2, 2, 1, 0x84, 4), "0009" + "0002" + "C0DE"));
        Assert.Equal(AckData("0501", "FF"), Convert.ToHexString(o.Response));
        Assert.Equal(0xC0DE, Word(device, 2));
    }

    // --- other functions and SZL --------------------------------------------------------------------------

    [Fact]
    public void UnknownFunction_IsAnsweredWithError8104()
    {
        var device = NewDevice();
        var o = Handle(device, Job("2900" + "0000000009505F50524F4752414D")); // PLC stop
        Assert.Equal(AckData("29", "", "8104"), Convert.ToHexString(o.Response));
        Assert.Equal(("cmd=29", "8104"), (o.Op, o.Error));
    }

    [Fact]
    public void Szl_ModuleIdentification_And_ComponentId()
    {
        var device = NewDevice();
        // userdata: param 00 01 12 04 11 44 01 00 (request, CPU functions, read SZL), data FF 09 0004 | SZL id 0011 | index 0000
        var o = Handle(device, "32070000" + "0100" + "0008" + "0008" + "0001120411440100" + "FF09" + "0004" + "0011" + "0000");
        var hex = Convert.ToHexString(o.Response);
        Assert.StartsWith("32070000" + "0100" + "000C", hex);                 // userdata response, 12-byte parameter
        Assert.Contains("000112081284010000000000", hex);                      // response parameter: method 12, 0x84 = response of CPU functions, subfunction 01, seq 0
        var data = o.Response[(10 + 12)..];   // 10-byte user data header + 12-byte parameter
        Assert.Equal((0xFF, 0x09), (data[0], data[1]));
        Assert.Equal((0x00, 0x11, 0x00, 0x00), (data[4], data[5], data[6], data[7]));   // SZL id and index echoed
        Assert.Equal((0x00, 0x1C), (data[8], data[9]));                               // 28-byte records
        int count = (data[10] << 8) | data[11];
        Assert.True(count >= 1);
        var first = data[12..(12 + 28)];
        Assert.Equal((0x00, 0x01), (first[0], first[1]));
        Assert.Equal("6ES7 315-2EH14-0AB0 ", Encoding.ASCII.GetString(first[2..22]));
        Assert.Equal(("szl", "0011", null), (o.Op, o.Target, o.Error));

        o = Handle(device, "32070000" + "0100" + "0008" + "0008" + "0001120411440100" + "FF09" + "0004" + "001C" + "0000");
        var text = Encoding.ASCII.GetString(o.Response);
        Assert.Contains("CPU 315-2 PN/DP", text);
        Assert.Contains("Original Siemens Equipment", text);
        Assert.Equal((0x00, 0x22), (o.Response[22 + 8], o.Response[22 + 9]));         // 34-byte records

        // an SZL id moz does not have → userdata response with error 8104 and no data
        o = Handle(device, "32070000" + "0100" + "0008" + "0008" + "0001120411440100" + "FF09" + "0004" + "0131" + "0001");
        hex = Convert.ToHexString(o.Response);
        Assert.StartsWith("32070000" + "0100" + "000C" + "0004", hex);
        Assert.EndsWith("8104" + "0A00" + "0000", hex);                               // error code in the parameter, data: return code 0A, no data
        Assert.Equal(("szl", "0131", "8104"), (o.Op, o.Target, o.Error));
    }

    [Fact]
    public void Malformed_Pdus_AreNotHandled()
    {
        var device = NewDevice();
        var maps = S7Map.Defaults(device);
        var session = new S7Session();
        Assert.False(S7Server.TryHandle(Convert.FromHexString("33010000010000020000F000"), device, maps, session, out _)); // protocol id
        Assert.False(S7Server.TryHandle(Convert.FromHexString("3201000001000002"), device, maps, session, out _));         // header cut short
        Assert.False(S7Server.TryHandle(Convert.FromHexString("32010000010000080000F000"), device, maps, session, out _));  // parameter shorter than announced
        Assert.False(S7Server.TryHandle(Convert.FromHexString("32030000010000000000" + "0000"), device, maps, session, out _)); // an ack, not a job
    }

    // --- ISO-on-TCP framing ----------------------------------------------------------------------------

    [Fact]
    public void Tpkt_And_Cotp_Parse_And_Build()
    {
        // TPKT: 03 00 length(2); COTP CR: length 11, E0, dst ref 0000, src ref 0001, class 00, params C1 02 0100 (src tsap), C2 02 0102 (dst tsap), C0 01 0A (tpdu 1024)
        var cr = Convert.FromHexString("0300" + "0016" + "11E0" + "0000" + "0001" + "00" + "C1020100" + "C2020102" + "C0010A");
        Assert.True(IsoTcp.TryParseTpkt(cr, out var length));
        Assert.Equal(0x16, length);
        Assert.True(IsoTcp.TryParseCotp(cr.AsSpan(4), out var code, out var payloadOffset));
        Assert.Equal((IsoTcp.ConnectRequest, 0x12), (code, payloadOffset));
        var cc = IsoTcp.ConnectConfirm(cr.AsSpan(4));
        // CC: the client's src ref becomes dst ref, our ref is 0001, class 0, the parameters (TSAPs, TPDU size) are echoed
        Assert.Equal("0300" + "0016" + "11D0" + "0001" + "0001" + "00" + "C1020100" + "C2020102" + "C0010A", Convert.ToHexString(cc));

        var dt = IsoTcp.Data(Convert.FromHexString("3201"));
        Assert.Equal("0300" + "0009" + "02F080" + "3201", Convert.ToHexString(dt));
        Assert.True(IsoTcp.TryParseCotp(dt.AsSpan(4), out code, out payloadOffset));
        Assert.Equal((IsoTcp.DataTransfer, 3), (code, payloadOffset));

        Assert.False(IsoTcp.TryParseTpkt(Convert.FromHexString("0200" + "0009"), out _));   // version
        Assert.False(IsoTcp.TryParseTpkt(Convert.FromHexString("0300" + "0006"), out _));   // shorter than TPKT + COTP
        Assert.False(IsoTcp.TryParseTpkt(Convert.FromHexString("0300" + "2000"), out _));   // over the frame limit
        Assert.False(IsoTcp.TryParseCotp(Convert.FromHexString("02F0"), out _, out _));     // COTP cut short
    }

    // --- runner (TCP) ------------------------------------------------------------------------------------

    private static async Task ExpectLogAsync(CapturingProvider provider, Predicate<CapturingProvider.Captured> predicate)
    {
        for (int i = 0; i < 50 && !provider.Entries.Exists(predicate); i++)
        {
            await Task.Delay(20);
        }
        Assert.Contains(provider.Entries, predicate);
    }

    private static async Task<byte[]> ExchangeAsync(NetworkStream stream, byte[] request)
    {
        await stream.WriteAsync(request);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(IsoTcp.TryParseTpkt(header, out var length));
        var rest = new byte[length - 4];
        await stream.ReadExactlyAsync(rest).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        return [.. header, .. rest];
    }

    private static async Task<(S7TcpServer Server, CapturingProvider Provider)> StartAsync(Device plc, CancellationToken ct, S7Map[]? maps = null)
    {
        var provider = new CapturingProvider();
        var server = new S7TcpServer("s7-srv", new IPEndPoint(IPAddress.Loopback, 0), plc, maps ?? S7Map.Defaults(plc), provider.CreateLogger("srv"));
        _ = server.RunAsync(ct);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        return (server, provider);
    }

    [Fact]
    public async Task Tcp_Connect_Setup_Read_Write()
    {
        var plc = NewDevice();
        plc.Seed(TableKind.Words, 0, new ushort[] { 0x1111, 0x2222 });
        using var cts = new CancellationTokenSource();
        var (server, provider) = await StartAsync(plc, cts.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();

        // COTP connect (rack 0 slot 2 TSAP 01 02)
        var cc = await ExchangeAsync(stream, Convert.FromHexString("0300" + "0016" + "11E0" + "0000" + "0002" + "00" + "C1020100" + "C2020102" + "C0010A"));
        Assert.Equal(0xD0, cc[5]);
        Assert.Equal((0x00, 0x02), (cc[6], cc[7]));
        // setup communication
        var setup = await ExchangeAsync(stream, IsoTcp.Data(Convert.FromHexString(Job("F000" + "0001" + "0001" + "01E0"))));
        Assert.EndsWith(AckData("F000" + "0001" + "0001" + "01E0"), Convert.ToHexString(setup));
        // read DB1.DBW0 x 2
        var read = await ExchangeAsync(stream, IsoTcp.Data(Convert.FromHexString(Job("0401" + Item(4, 2, 1, 0x84, 0)))));
        Assert.EndsWith(AckData("0401", "FF04" + "0020" + "11112222"), Convert.ToHexString(read));
        // write DB1.DBW4 = BEEF
        var write = await ExchangeAsync(stream, IsoTcp.Data(Convert.FromHexString(Job("0501" + Item(4, 1, 1, 0x84, 4), "0004" + "0010" + "BEEF"))));
        Assert.EndsWith(AckData("0501", "FF"), Convert.ToHexString(write));
        Assert.Equal(0xBEEF, Word(plc, 2));

        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnAccepted.Id);
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id && e.Message.Contains("op=setup"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("target=DB1.DBW0") && e.Message.Contains("qty=1") && e.Message.Contains("result=ok"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=write") && e.Message.Contains("target=DB1.DBW4"));
        cts.Cancel();
    }

    [Fact]
    public async Task Tcp_DataBeforeConnect_IsServed_And_BadTpkt_Closes()
    {
        var plc = NewDevice();
        using var cts = new CancellationTokenSource();
        var (server, provider) = await StartAsync(plc, cts.Token);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(server.BoundEndpoint!);
            var stream = client.GetStream();
            // no COTP connect: tolerated (a setup is answered)
            var setup = await ExchangeAsync(stream, IsoTcp.Data(Convert.FromHexString(Job("F000" + "0001" + "0001" + "01E0"))));
            Assert.EndsWith("01E0", Convert.ToHexString(setup));
            // a Modbus frame is not TPKT: closed without an answer
            await stream.WriteAsync(Convert.FromHexString("000100000006010300000002"));
            var buf = new byte[16];
            using var readCts = new CancellationTokenSource(3000);
            try
            {
                Assert.Equal(0, await stream.ReadAsync(buf, readCts.Token));
            }
            catch (IOException)
            {
            }
        }
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.MalformedFrame.Id);
        cts.Cancel();
    }
}
