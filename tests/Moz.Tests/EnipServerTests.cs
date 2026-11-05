using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.EtherNetIp;

namespace Moz.Tests;

/// <summary>
/// EtherNet/IP tag server (phase 23): the pure CIP router over a
/// VariableTable (tags = variables), the encapsulation layer, and the TCP
/// runner with a hand-written client (register session, unconnected send,
/// forward open, connected send).
/// </summary>
public class EnipServerTests
{
    private static (Device Device, VariableTable Vars) Fixture()
    {
        var device = new Device("plc", bits: 64, readOnlyBits: 16, words: 256, readOnlyWords: 16);
        var vars = new VariableTable(device,
        [
            new Variable("temp", TableKind.Words, 0, VariableType.Real),
            new Variable("counter", TableKind.Words, 2, VariableType.Dint),
            new Variable("flags", TableKind.Bits, 0, VariableType.Bool, Count: 4),
            new Variable("samples", TableKind.Words, 10, VariableType.Int, Count: 3),
            new Variable("label", TableKind.Words, 20, VariableType.String, Length: 8),
            new Variable("ro", TableKind.ReadOnlyWords, 0, VariableType.Int),
            new Variable("motor.speed", TableKind.Words, 30, VariableType.Uint),
        ], NullLogger.Instance);
        return (device, vars);
    }

    private sealed class FakeConnections : ICipConnections
    {
        public readonly List<(uint ToTarget, uint ToOriginator, ushort Serial)> Open = [];
        private uint _next = 0x10000000;

        public uint OpenConnection(uint toOriginatorId, ushort serial, ushort vendor, uint originatorSerial, int size)
        {
            var id = ++_next;
            Open.Add((id, toOriginatorId, serial));
            return id;
        }

        public bool CloseConnection(ushort serial, ushort vendor, uint originatorSerial) => Open.RemoveAll(c => c.Serial == serial) > 0;
    }

    private static CipOutcome Route(VariableTable vars, string requestHex, ICipConnections? connections = null, int maxReply = 504) =>
        CipRouter.Handle(Convert.FromHexString(requestHex), new CipContext(vars, "plc-ua", connections ?? new FakeConnections(), maxReply));

    /// <summary>Symbolic path "name" (+ optional element index) as a padded EPATH with its word count.</summary>
    private static string TagPath(string name, int? element = null)
    {
        var bytes = new List<byte> { 0x91, (byte)name.Length };
        bytes.AddRange(Encoding.ASCII.GetBytes(name));
        if (name.Length % 2 == 1)
        {
            bytes.Add(0);
        }
        if (element is { } e)
        {
            bytes.AddRange([0x28, (byte)e]);
        }
        return $"{bytes.Count / 2:X2}" + Convert.ToHexString(bytes.ToArray());
    }

    private static string Le16(int v) => $"{v & 0xFF:X2}{(v >> 8) & 0xFF:X2}";
    private static string Le32(uint v) => $"{v & 0xFF:X2}{(v >> 8) & 0xFF:X2}{(v >> 16) & 0xFF:X2}{(v >> 24) & 0xFF:X2}";

    // --- tags: read ------------------------------------------------------------------------------------

    [Fact]
    public void ReadTag_Scalar_Types()
    {
        var (_, vars) = Fixture();
        vars.Write("temp", 1.5f);
        vars.Write("counter", -2);
        var o = Route(vars, "4C" + TagPath("temp") + "0100");
        Assert.Equal("CC000000" + "CA00" + "0000C03F", Convert.ToHexString(o.Reply));
        Assert.Equal(("read", "temp", 1, null), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal("CC000000" + "C400" + "FEFFFFFF", Convert.ToHexString(Route(vars, "4C" + TagPath("counter") + "0100").Reply));
        // no element count in the request: one element
        Assert.Equal("CC000000" + "C400" + "FEFFFFFF", Convert.ToHexString(Route(vars, "4C" + TagPath("counter")).Reply));
        vars.Write("motor.speed", 7u);
        Assert.Equal("CC000000" + "C700" + "0700", Convert.ToHexString(Route(vars, "4C" + TagPath("motor.speed") + "0100").Reply));
        // the same tag addressed as two symbolic segments (member access)
        var path = "91056D6F746F7200" + "9105737065656400";
        Assert.Equal("CC000000" + "C700" + "0700", Convert.ToHexString(Route(vars, "4C" + "08" + path + "0100").Reply));
    }

    [Fact]
    public void ReadTag_Arrays_And_Elements()
    {
        var (_, vars) = Fixture();
        vars.WriteAll("samples", new[] { 1, -1, 300 });
        vars.WriteAll("flags", new[] { true, false, true, true });
        Assert.Equal("CC000000" + "C300" + "0100FFFF2C01", Convert.ToHexString(Route(vars, "4C" + TagPath("samples") + "0300").Reply));
        Assert.Equal("CC000000" + "C300" + "FFFF2C01", Convert.ToHexString(Route(vars, "4C" + TagPath("samples", 1) + "0200").Reply));
        var o = Route(vars, "4C" + TagPath("flags") + "0400");
        Assert.Equal("CC000000" + "C100" + "01000101", Convert.ToHexString(o.Reply));
        Assert.Equal(("flags", 4), (o.Target, o.Points));
        // element 1 + 3 elements runs past the end: FF with extended 2105
        o = Route(vars, "4C" + TagPath("samples", 1) + "0300");
        Assert.Equal("CC00FF01" + "0521", Convert.ToHexString(o.Reply));
        Assert.Equal("FF:2105", o.Error);
        Assert.Equal("CC00FF01" + "0521", Convert.ToHexString(Route(vars, "4C" + TagPath("samples") + "0000").Reply)); // zero elements
    }

    [Fact]
    public void ReadTag_String_IsLogixStringStructure()
    {
        var (_, vars) = Fixture();
        vars.Write("label", "hi");
        var o = Route(vars, "4C" + TagPath("label") + "0100");
        var hex = Convert.ToHexString(o.Reply);
        Assert.StartsWith("CC000000" + "A002" + "CE0F" + "02000000" + "6869", hex);
        Assert.Equal(8 + 4 + 82 + 2, o.Reply.Length); // reply header + LEN + DATA[82] + pad
    }

    [Fact]
    public void ReadTag_Unknown_And_BadPath()
    {
        var (_, vars) = Fixture();
        var o = Route(vars, "4C" + TagPath("nothere") + "0100");
        Assert.Equal("CC000400", Convert.ToHexString(o.Reply));
        Assert.Equal(("read", "nothere", "04"), (o.Op, o.Target, o.Error));
        Assert.Equal("CC000400", Convert.ToHexString(Route(vars, "4C" + "02" + "9105" + "7465" + "0100").Reply)); // symbol segment cut short
    }

    [Fact]
    public void ReadTagFragmented_ReturnsPartial_With06()
    {
        var (_, vars) = Fixture();
        vars.WriteAll("samples", new[] { 1, 2, 3 });
        // offset 2 bytes: elements 1..2
        Assert.Equal("D2000000" + "C300" + "02000300", Convert.ToHexString(Route(vars, "52" + TagPath("samples") + "0300" + "02000000").Reply));
        // a reply budget of 10 bytes: header 4 + type 2 + 4 bytes of data → partial transfer
        var o = Route(vars, "52" + TagPath("samples") + "0300" + "00000000", maxReply: 10);
        Assert.Equal("D2000600" + "C300" + "01000200", Convert.ToHexString(o.Reply));
        Assert.Equal(("read-frag", "samples", 3, "06"), (o.Op, o.Target, o.Points, o.Error));
        o = Route(vars, "52" + TagPath("samples") + "0300" + "04000000", maxReply: 10);
        Assert.Equal("D2000000" + "C300" + "0300", Convert.ToHexString(o.Reply));
    }

    // --- tags: write ----------------------------------------------------------------------------------

    [Fact]
    public void WriteTag_Scalar_Array_Element_String()
    {
        var (_, vars) = Fixture();
        var o = Route(vars, "4D" + TagPath("temp") + "CA00" + "0100" + "0000C03F");
        Assert.Equal("CD000000", Convert.ToHexString(o.Reply));
        Assert.Equal(("write", "temp", 1, null), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal(1.5f, vars.Read<float>("temp"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + TagPath("samples") + "C300" + "0300" + "0100FFFF2C01").Reply));
        Assert.Equal([1, -1, 300], vars.ReadAll<int>("samples"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + TagPath("samples", 2) + "C300" + "0100" + "0500").Reply));
        Assert.Equal([1, -1, 5], vars.ReadAll<int>("samples"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + TagPath("flags", 1) + "C100" + "0200" + "0101").Reply));
        Assert.Equal([false, true, true, false], vars.ReadAll<bool>("flags"));
        var str = "A002CE0F" + "0100" + "03000000" + "616263" + new string('0', (82 - 3) * 2) + "0000";
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + TagPath("label") + str).Reply));
        Assert.Equal("abc", vars.Read<string>("label"));
    }

    [Theory]
    [InlineData("4D{temp}C3000100" + "0000", "CD00FF01" + "0721", "FF:2107", "type mismatch")]
    [InlineData("4D{ro}C3000100" + "0100", "CD000F00", "0F", "read-only table")]
    [InlineData("4D{nothere}C3000100" + "0100", "CD000400", "04", "unknown tag")]
    [InlineData("4D{samples}C3000400" + "0100020003000400", "CD00FF01" + "0521", "FF:2105", "too many elements")]
    [InlineData("4D{samples}C3000200" + "0100", "CD001300", "13", "not enough data")]
    [InlineData("4D{samples}C3000100" + "01000200", "CD001500", "15", "too much data")]
    [InlineData("4D{label}A002CE0F0100" + "FFFFFFFF" + "00", "CD001300", "13", "string structure cut short")]
    public void WriteTag_Errors(string requestTemplate, string reply, string error, string why)
    {
        var (_, vars) = Fixture();
        var request = requestTemplate.Replace("{temp}", TagPath("temp")).Replace("{ro}", TagPath("ro"))
            .Replace("{nothere}", TagPath("nothere")).Replace("{samples}", TagPath("samples")).Replace("{label}", TagPath("label"));
        var o = Route(vars, request);
        Assert.True(reply == Convert.ToHexString(o.Reply) && error == o.Error, $"{why}: got {Convert.ToHexString(o.Reply)} / {o.Error}");
    }

    [Fact]
    public void WriteTagFragmented_WritesFromOffset()
    {
        var (_, vars) = Fixture();
        var o = Route(vars, "53" + TagPath("samples") + "C300" + "0300" + "02000000" + "0A000B00");
        Assert.Equal("D3000000", Convert.ToHexString(o.Reply));
        Assert.Equal(("write-frag", "samples", 3, null), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal([0, 10, 11], vars.ReadAll<int>("samples"));
        Assert.Equal("D3002000", Convert.ToHexString(Route(vars, "53" + TagPath("samples") + "C300" + "0300" + "01000000" + "0A00").Reply)); // unaligned offset
    }

    [Fact]
    public void UnsupportedTagService_Is08()
    {
        var (_, vars) = Fixture();
        Assert.Equal("CE000800", Convert.ToHexString(Route(vars, "4E" + TagPath("counter") + "0000").Reply)); // read-modify-write
    }

    // --- objects --------------------------------------------------------------------------------------

    [Fact]
    public void Identity_GetAttributesAll_And_Single()
    {
        var (_, vars) = Fixture();
        var o = Route(vars, "01" + "02" + "2001" + "2401");
        var reply = o.Reply;
        Assert.Equal("81000000", Convert.ToHexString(reply[..4]));
        var body = reply[4..];
        Assert.Equal(CipRouter.VendorId, BinaryPrimitives.ReadUInt16LittleEndian(body));
        Assert.Equal(0x000E, BinaryPrimitives.ReadUInt16LittleEndian(body[2..]));            // programmable logic controller
        Assert.Equal((20, 1), (body[6], body[7]));                                           // revision 20.1
        var name = Encoding.ASCII.GetString(body[15..(15 + body[14])]);
        Assert.Equal("moz plc-ua", name);
        Assert.Equal(("identity", "01", null), (o.Op, o.Target, o.Error));
        // attribute 7 (product name) single
        Assert.Equal("8E000000" + "0A" + Convert.ToHexString(Encoding.ASCII.GetBytes("moz plc-ua")), Convert.ToHexString(Route(vars, "0E" + "03" + "2001" + "2401" + "3007").Reply));
        Assert.Equal("8E001400", Convert.ToHexString(Route(vars, "0E" + "03" + "2001" + "2401" + "3009").Reply)); // attribute not supported
    }

    [Fact]
    public void ProgramName_IsReturnedAsString()
    {
        var (_, vars) = Fixture();
        var o = Route(vars, "01" + "02" + "2064" + "2401");
        Assert.Equal("81000000" + "0A00" + Convert.ToHexString(Encoding.ASCII.GetBytes("moz plc-ua")), Convert.ToHexString(o.Reply));
    }

    [Fact]
    public void UnknownClass_And_Service()
    {
        var (_, vars) = Fixture();
        Assert.Equal("81000500", Convert.ToHexString(Route(vars, "01" + "02" + "2099" + "2401").Reply));   // path destination unknown
        Assert.Equal("90000800", Convert.ToHexString(Route(vars, "10" + "02" + "2001" + "2401" + "0000").Reply)); // set attribute single on identity
        var o = Route(vars, "03" + "01" + "2001");
        Assert.Equal(("cmd=03", "08"), (o.Op, o.Error));
    }

    [Fact]
    public void MultipleServicePacket_RoutesEachRequest()
    {
        var (_, vars) = Fixture();
        vars.Write("counter", 5);
        var r1 = Convert.FromHexString("4C" + TagPath("counter") + "0100");
        var r2 = Convert.FromHexString("4C" + TagPath("nothere") + "0100");
        var data = new List<byte> { 2, 0 };
        int off1 = 2 + 4, off2 = off1 + r1.Length;
        data.AddRange([(byte)off1, 0, (byte)off2, 0]);
        data.AddRange(r1);
        data.AddRange(r2);
        var o = Route(vars, "0A" + "02" + "2002" + "2401" + Convert.ToHexString(data.ToArray()));
        var reply1 = "CC000000" + "C400" + "05000000";
        var reply2 = "CC000400";
        Assert.Equal("8A000000" + "0200" + Le16(6) + Le16(6 + reply1.Length / 2) + reply1 + reply2, Convert.ToHexString(o.Reply));
        Assert.Equal(("multi", "counter", 2, "04"), (o.Op, o.Target, o.Points, o.Error));
    }

    [Fact]
    public void SymbolList_ReturnsTags_WithRequestedAttributes()
    {
        var (_, vars) = Fixture();
        // pylogix style: attributes 1 (name), 2 (type), 8 (dimensions), from instance 0
        var o = Route(vars, "55" + "03" + "206B" + "2500" + "0000" + "0300" + "0100" + "0200" + "0800");
        Assert.Equal("D5000000", Convert.ToHexString(o.Reply[..4]));
        var body = o.Reply[4..];
        int at = 0;
        var seen = new List<(uint Id, string Name, ushort Type, uint Dim)>();
        while (at < body.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 4));
            var name = Encoding.ASCII.GetString(body, at + 6, len);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at + 6 + len));
            uint dim = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 8 + len));
            seen.Add((id, name, type, dim));
            at += 4 + 2 + len + 2 + 12;
        }
        Assert.Equal(7, seen.Count);
        Assert.Contains((1u, "temp", (ushort)0x00CA, 0u), seen);
        Assert.Contains((3u, "flags", (ushort)0x20C1, 4u), seen);      // array bit + BOOL, dimension 4
        Assert.Contains((4u, "samples", (ushort)0x20C3, 3u), seen);
        Assert.Contains((5u, "label", (ushort)0x8FCE, 0u), seen);      // structure bit + STRING handle
        Assert.Contains((7u, "motor.speed", (ushort)0x00C7, 0u), seen);
        Assert.Equal(("tags", "from=0", 7, null), (o.Op, o.Target, o.Points, o.Error));

        // pycomm3 style: 1, 2, 3, 5, 6, 8, 10 from instance 6 (inclusive) → two tags left, each 4 + 2 + len + 2 + 4 + 4 + 4 + 12 + 1 bytes
        o = Route(vars, "55" + "03" + "206B" + "2500" + "0600" + "0700" + "0100" + "0200" + "0300" + "0500" + "0600" + "0800" + "0A00");
        Assert.Equal("D5000000", Convert.ToHexString(o.Reply[..4]));
        Assert.Equal((4 + 2 + 2 + 2 + 4 + 4 + 4 + 12 + 1) + (4 + 2 + 11 + 2 + 4 + 4 + 4 + 12 + 1), o.Reply.Length - 4);
        Assert.Equal(1, o.Reply[4 + 4 + 2 + 2 + 2 + 4 + 4 + 4 + 12]); // "ro": external access 1 = read only
        Assert.Equal(0, o.Reply[^1]);                                // "motor.speed": read/write

        // a reply budget too small for everything: partial transfer 06 with the tags that fit
        o = Route(vars, "55" + "03" + "206B" + "2500" + "0000" + "0300" + "0100" + "0200" + "0800", maxReply: 60);
        Assert.Equal("D5000600", Convert.ToHexString(o.Reply[..4]));
        Assert.Equal("06", o.Error);
        // beyond the last instance: empty success
        Assert.Equal("D5000000", Convert.ToHexString(Route(vars, "55" + "03" + "206B" + "2500" + "0800" + "0100" + "0100").Reply));
        // an attribute moz does not have
        Assert.Equal("D5001400", Convert.ToHexString(Route(vars, "55" + "03" + "206B" + "2500" + "0000" + "0100" + "0900").Reply));
    }

    [Fact]
    public void Template_DescribesTheStringStructure()
    {
        var (_, vars) = Fixture();
        // get attribute list 4 (definition size), 5 (structure size), 2 (member count), 1 (handle) on template 0x0FCE
        var o = Route(vars, "03" + "03" + "206C" + "2500" + "CE0F" + "0400" + "0400" + "0500" + "0200" + "0100");
        // definition size 14: clients read 14 * 4 - 21 = 35 bytes, enough for the 32-byte definition below
        Assert.Equal("83000000" + "0400" + "0400" + "0000" + Le32(14) + "0500" + "0000" + Le32(88) + "0200" + "0000" + "0200" + "0100" + "0000" + "CE0F", Convert.ToHexString(o.Reply));
        Assert.Equal(("template", "0FCE", null), (o.Op, o.Target, o.Error));
        // read template: offset 0, 35 bytes requested → the whole 32-byte definition
        o = Route(vars, "4C" + "03" + "206C" + "2500" + "CE0F" + "00000000" + "2300");
        var body = o.Reply[4..];
        Assert.Equal("CC000000", Convert.ToHexString(o.Reply[..4]));
        Assert.Equal("0000" + "C400" + "00000000" + "5200" + "C220" + "04000000", Convert.ToHexString(body[..16])); // LEN: DINT @0; DATA: SINT[82] @4
        Assert.Equal("STRING\0LEN\0DATA\0", Encoding.ASCII.GetString(body[16..]));
        Assert.Equal("83000500", Convert.ToHexString(Route(vars, "03" + "03" + "206C" + "2500" + "1234" + "0100" + "0100").Reply)); // unknown template
    }

    [Fact]
    public void ForwardOpen_Close_And_UnconnectedSend()
    {
        var (_, vars) = Fixture();
        vars.Write("counter", 9);
        var connections = new FakeConnections();
        // large forward open: tick, timeout, O->T id 0, T->O id, serial, vendor, originator serial, multiplier, reserved, RPI, params (4), RPI, params (4), transport, path size, path
        var open = "5B" + "02" + "2006" + "2401" + "0A" + "05" + "00000000" + "78563412" + "0100" + "3412" + "11223344" + "07" + "000000"
            + "01402000" + "F4010042" + "01402000" + "F4010042" + "A3" + "03" + "010020022401";
        var o = Route(vars, open, connections);
        Assert.Equal("DB000000", Convert.ToHexString(o.Reply[..4]));
        var body = o.Reply[4..];
        Assert.Equal(26, body.Length);
        uint toTarget = BinaryPrimitives.ReadUInt32LittleEndian(body);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal("0100" + "3412" + "11223344" + "01402000" + "01402000" + "00" + "00", Convert.ToHexString(body[8..]));
        Assert.Single(connections.Open);
        Assert.Equal((toTarget, 0x12345678u, (ushort)1), connections.Open[0]);
        Assert.Equal(("forward-open", "size=500", 1, null), (o.Op, o.Target, o.Points, o.Error));

        // unconnected send wrapping a read: tick, timeout, embedded size, embedded request, (pad), route size, reserved, route
        var embedded = Convert.FromHexString("4C" + TagPath("counter") + "0100");
        var wrapped = "52" + "02" + "2006" + "2401" + "0A05" + Le16(embedded.Length) + Convert.ToHexString(embedded) + (embedded.Length % 2 == 1 ? "00" : "") + "01" + "00" + "0100";
        Assert.Equal("CC000000" + "C400" + "09000000", Convert.ToHexString(Route(vars, wrapped, connections).Reply));

        // forward close by serial / vendor / originator serial
        o = Route(vars, "4E" + "02" + "2006" + "2401" + "0A05" + "0100" + "3412" + "11223344" + "03" + "00" + "010020022401", connections);
        Assert.Equal("CE000000" + "0100" + "3412" + "11223344" + "00" + "00", Convert.ToHexString(o.Reply));
        Assert.Empty(connections.Open);
        Assert.Equal(("forward-close", "serial=1", 0, null), (o.Op, o.Target, o.Points, o.Error));
        // closing again: connection not found (01 / 0107)
        Assert.Equal("CE000101" + "0701", Convert.ToHexString(Route(vars, "4E" + "02" + "2006" + "2401" + "0A05" + "0100" + "3412" + "11223344" + "03" + "00" + "010020022401", connections).Reply));
        // standard forward open (2-byte parameters)
        o = Route(vars, "54" + "02" + "2006" + "2401" + "0A05" + "00000000" + "78563412" + "0200" + "3412" + "11223344" + "07" + "000000"
            + "01402000" + "F443" + "01402000" + "F443" + "A3" + "03" + "010020022401", connections);
        Assert.Equal("D4000000", Convert.ToHexString(o.Reply[..4]));
        Assert.Equal("size=500", o.Target);
    }

    // --- encapsulation ----------------------------------------------------------------------------------

    [Fact]
    public void Encapsulation_Header_ParseAndBuild()
    {
        var frame = Encap.Build(Encap.RegisterSession, 0x11223344, 0, Convert.FromHexString("0102030405060708"), Convert.FromHexString("01000000"));
        Assert.Equal("6500" + "0400" + "44332211" + "00000000" + "0102030405060708" + "00000000" + "01000000", Convert.ToHexString(frame));
        Assert.True(Encap.TryParseHeader(frame, out var command, out var length, out var session, out var status));
        Assert.Equal((Encap.RegisterSession, 4, 0x11223344u, 0u), (command, length, session, status));
        Assert.False(Encap.TryParseHeader(frame[..20], out _, out _, out _, out _));
        Assert.False(Encap.TryParseHeader(Convert.FromHexString("6500" + "FFFF" + "00000000" + "00000000" + "0000000000000000" + "00000000"), out _, out _, out _, out _));
    }

    // --- runner -----------------------------------------------------------------------------------------

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
        var header = new byte[Encap.HeaderSize];
        await stream.ReadExactlyAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(Encap.TryParseHeader(header, out _, out var length, out _, out _));
        var data = new byte[length];
        await stream.ReadExactlyAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        return [.. header, .. data];
    }

    private static byte[] SendRRData(uint session, byte[] cip)
    {
        var cpf = new List<byte>();
        cpf.AddRange([0, 0, 0, 0, 0, 0]);          // interface handle, timeout
        cpf.AddRange([2, 0, 0, 0, 0, 0]);          // 2 items: null address
        cpf.AddRange([0xB2, 0, (byte)cip.Length, (byte)(cip.Length >> 8)]);
        cpf.AddRange(cip);
        return Encap.Build(Encap.SendRRData, session, 0, new byte[8], cpf.ToArray());
    }

    private static byte[] SendUnitData(uint session, uint connection, ushort sequence, byte[] cip)
    {
        var cpf = new List<byte>();
        cpf.AddRange([0, 0, 0, 0, 0, 0]);
        cpf.AddRange([2, 0, 0xA1, 0, 4, 0]);
        cpf.AddRange(BitConverter.GetBytes(connection));
        cpf.AddRange([0xB1, 0, (byte)(cip.Length + 2), (byte)((cip.Length + 2) >> 8)]);
        cpf.AddRange(BitConverter.GetBytes(sequence));
        cpf.AddRange(cip);
        return Encap.Build(Encap.SendUnitData, session, 0, new byte[8], cpf.ToArray());
    }

    [Fact]
    public async Task Tcp_Register_Unconnected_Connected_Unregister()
    {
        var (_, vars) = Fixture();
        vars.Write("counter", 42);
        var provider = new CapturingProvider();
        var server = new EnipServer("enip-srv", new IPEndPoint(IPAddress.Loopback, 0), vars, provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();

        // list identity needs no session
        var identity = await ExchangeAsync(stream, Encap.Build(Encap.ListIdentity, 0, 0, new byte[8], []));
        Assert.Contains("moz enip-srv", Encoding.ASCII.GetString(identity));

        // register session
        var reg = await ExchangeAsync(stream, Encap.Build(Encap.RegisterSession, 0, 0, new byte[8], Convert.FromHexString("01000000")));
        Assert.True(Encap.TryParseHeader(reg, out _, out _, out var session, out var status));
        Assert.Equal(0u, status);
        Assert.NotEqual(0u, session);

        // unconnected read wrapped in an unconnected send
        var embedded = Convert.FromHexString("4C" + TagPath("counter") + "0100");
        var wrapped = Convert.FromHexString("52" + "02" + "2006" + "2401" + "0A05" + Le16(embedded.Length) + Convert.ToHexString(embedded) + (embedded.Length % 2 == 1 ? "00" : "") + "01" + "00" + "0100");
        var rr = await ExchangeAsync(stream, SendRRData(session, wrapped));
        Assert.EndsWith("B200" + "0A00" + "CC000000" + "C400" + "2A000000", Convert.ToHexString(rr));

        // a wrong session handle is refused
        var bad = await ExchangeAsync(stream, SendRRData(session + 1, wrapped));
        Assert.True(Encap.TryParseHeader(bad, out _, out _, out _, out status));
        Assert.Equal(0x64u, status);

        // forward open, then a connected write + read
        var open = Convert.FromHexString("5B" + "02" + "2006" + "2401" + "0A" + "05" + "00000000" + "78563412" + "0100" + "3412" + "11223344" + "07" + "000000"
            + "01402000" + "F4010042" + "01402000" + "F4010042" + "A3" + "03" + "010020022401");
        var fo = await ExchangeAsync(stream, SendRRData(session, open));
        var foReply = fo[(fo.Length - 26 - 4)..];
        Assert.Equal("DB000000", Convert.ToHexString(foReply[..4]));
        uint toTarget = BinaryPrimitives.ReadUInt32LittleEndian(foReply.AsSpan(4));

        var write = Convert.FromHexString("4D" + TagPath("counter") + "C400" + "0100" + "07000000");
        var ud = await ExchangeAsync(stream, SendUnitData(session, toTarget, 1, write));
        Assert.EndsWith("A100" + "0400" + "78563412" + "B100" + "0600" + "0100" + "CD000000", Convert.ToHexString(ud));
        Assert.Equal(7, vars.Read<int>("counter"));
        var read = await ExchangeAsync(stream, SendUnitData(session, toTarget, 2, embedded));
        Assert.EndsWith("0200" + "CC000000" + "C400" + "07000000", Convert.ToHexString(read));

        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id && e.Message.Contains("op=forward-open"));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=write") && e.Message.Contains("target=counter") && e.Message.Contains("result=ok"));

        // unregister: the server closes
        await stream.WriteAsync(Encap.Build(Encap.UnRegisterSession, session, 0, new byte[8], []));
        var buf = new byte[16];
        using var readCts = new CancellationTokenSource(3000);
        try
        {
            Assert.Equal(0, await stream.ReadAsync(buf, readCts.Token));
        }
        catch (IOException)
        {
        }
        cts.Cancel();
    }

    [Fact]
    public async Task Tcp_BadEncapsulation_Closes_LogsMalformed()
    {
        var (_, vars) = Fixture();
        var provider = new CapturingProvider();
        var server = new EnipServer("enip-srv", new IPEndPoint(IPAddress.Loopback, 0), vars, provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString("6500" + "FFFF" + "00000000" + "00000000" + "0000000000000000" + "00000000"));
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
