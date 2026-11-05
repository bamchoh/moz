using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.EtherNetIp;

namespace Moz.Tests;

/// <summary>
/// EtherNet/IP tag server, Omron NJ/NX dialect (phase 24): the pure CIP
/// router (W506 7-6 / 7-7 variable services and data formats, Tag Name
/// Server 6A, Variable Object 6B, Variable Type Object 6C, Simple Data
/// Segment) and the TCP runner with the NJ identity.
/// </summary>
public class EnipNjServerTests
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
            new Variable("pump", TableKind.Bits, 10, VariableType.Bool),
            new Variable("dur", TableKind.Words, 40, VariableType.Time),
            new Variable("stamp", TableKind.Words, 42, VariableType.Dt),
        ], NullLogger.Instance);
        return (device, vars);
    }

    private sealed class FakeConnections : ICipConnections
    {
        public readonly List<(uint ToTarget, uint ToOriginator, ushort Serial, int Size)> Open = [];
        private uint _next = 0x10000000;

        public uint OpenConnection(uint toOriginatorId, ushort serial, ushort vendor, uint originatorSerial, int size)
        {
            var id = ++_next;
            Open.Add((id, toOriginatorId, serial, size));
            return id;
        }

        public bool CloseConnection(ushort serial, ushort vendor, uint originatorSerial) => Open.RemoveAll(c => c.Serial == serial) > 0;
    }

    private static CipOutcome Route(VariableTable vars, string requestHex, ICipConnections? connections = null, int maxReply = 502) =>
        NjCipRouter.Handle(Convert.FromHexString(requestHex), new CipContext(vars, CipIdentity.OmronNj(), connections ?? new FakeConnections(), maxReply));

    /// <summary>Symbolic path "name" (+ optional element index, + optional simple data segment offset/size) as a padded EPATH with its word count.</summary>
    private static string Path(string name, int? element = null, (int Offset, int Size)? segment = null)
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
        if (segment is { } s)
        {
            bytes.AddRange([0x80, 0x03]);
            bytes.AddRange(BitConverter.GetBytes((uint)s.Offset));
            bytes.AddRange(BitConverter.GetBytes((ushort)s.Size));
        }
        return $"{bytes.Count / 2:X2}" + Convert.ToHexString(bytes.ToArray());
    }

    private static string Hex(string ascii) => Convert.ToHexString(Encoding.ASCII.GetBytes(ascii));

    // --- tags: read ------------------------------------------------------------------------------------

    [Fact]
    public void ReadTag_Scalars_And_TimeTypes()
    {
        var (_, vars) = Fixture();
        vars.Write("temp", 1.5f);
        vars.Write("counter", -2);
        var o = Route(vars, "4C" + Path("temp") + "0100");
        Assert.Equal("CC000000" + "CA00" + "0000C03F", Convert.ToHexString(o.Reply));
        Assert.Equal(("read", "temp", 1, null), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal("CC000000" + "C400" + "FEFFFFFF", Convert.ToHexString(Route(vars, "4C" + Path("counter") + "0100").Reply));
        // Num of Element is mandatory on the NJ
        o = Route(vars, "4C" + Path("counter"));
        Assert.Equal(("CC001300", "13"), (Convert.ToHexString(o.Reply), o.Error));
        // more than one element of a scalar: 20 + 8017
        o = Route(vars, "4C" + Path("counter") + "0200");
        Assert.Equal(("CC002001" + "1780", "20:8017"), (Convert.ToHexString(o.Reply), o.Error));
        // time types are 8-byte nanosecond counts with Omron's vendor codes
        vars.Write("dur", 1500);
        Assert.Equal("CC000000" + "0900" + "002F685900000000", Convert.ToHexString(Route(vars, "4C" + Path("dur") + "0100").Reply));
        vars.Write("stamp", 1UL);
        Assert.Equal("CC000000" + "0A00" + "00CA9A3B00000000", Convert.ToHexString(Route(vars, "4C" + Path("stamp") + "0100").Reply));
        // member access as two symbolic segments
        vars.Write("motor.speed", 7u);
        Assert.Equal("CC000000" + "C700" + "0700", Convert.ToHexString(Route(vars, "4C" + "08" + "91056D6F746F7200" + "9105737065656400" + "0100").Reply));
    }

    [Fact]
    public void ReadTag_Bool_Elementary_Packed_And_Range()
    {
        var (_, vars) = Fixture();
        vars.Write("pump", true);
        vars.WriteAll("flags", new[] { true, false, true, true });
        // one BOOL: status + forced flag
        Assert.Equal("CC000000" + "C100" + "0100", Convert.ToHexString(Route(vars, "4C" + Path("pump") + "0100").Reply));
        // the whole array (no element): bits packed into words, Num of Element 1 or the array length
        var o = Route(vars, "4C" + Path("flags") + "0400");
        Assert.Equal("CC000000" + "C100" + "0D00", Convert.ToHexString(o.Reply));
        Assert.Equal(("flags", 4), (o.Target, o.Points));
        Assert.Equal("CC000000" + "C100" + "0D00", Convert.ToHexString(Route(vars, "4C" + Path("flags") + "0100").Reply));
        Assert.Equal("CC002001" + "1880", Convert.ToHexString(Route(vars, "4C" + Path("flags") + "0200").Reply));
        // one element: elementary format; a range from an element: one status byte per element
        Assert.Equal("CC000000" + "C100" + "0000", Convert.ToHexString(Route(vars, "4C" + Path("flags", 1) + "0100").Reply));
        Assert.Equal("CC000000" + "C100" + "000101", Convert.ToHexString(Route(vars, "4C" + Path("flags", 1) + "0300").Reply));
        o = Route(vars, "4C" + Path("flags", 2) + "0300");
        Assert.Equal(("CC002001" + "1880", "20:8018"), (Convert.ToHexString(o.Reply), o.Error));
    }

    [Fact]
    public void ReadTag_Arrays_And_String()
    {
        var (_, vars) = Fixture();
        vars.WriteAll("samples", new[] { 1, -1, 300 });
        Assert.Equal("CC000000" + "C300" + "0100FFFF2C01", Convert.ToHexString(Route(vars, "4C" + Path("samples") + "0300").Reply));
        Assert.Equal("CC000000" + "C300" + "FFFF2C01", Convert.ToHexString(Route(vars, "4C" + Path("samples", 1) + "0200").Reply));
        Assert.Equal("CC002001" + "1880", Convert.ToHexString(Route(vars, "4C" + Path("samples", 1) + "0300").Reply));
        Assert.Equal("CC002001" + "1880", Convert.ToHexString(Route(vars, "4C" + Path("samples") + "0000").Reply));
        // STRING: D0, then the CIP STRING (UINT byte count + UTF-8 bytes)
        vars.Write("label", "hi");
        var o = Route(vars, "4C" + Path("label") + "0100");
        Assert.Equal("CC000000" + "D000" + "0200" + "6869", Convert.ToHexString(o.Reply));
        Assert.Equal(("read", "label", 1, null), (o.Op, o.Target, o.Points, o.Error));
        // unknown tag / broken path
        o = Route(vars, "4C" + Path("nothere") + "0100");
        Assert.Equal(("CC000400", "04"), (Convert.ToHexString(o.Reply), o.Error));
        Assert.Equal("CC000400", Convert.ToHexString(Route(vars, "4C" + "02" + "9105" + "7465" + "0100").Reply));
        // the reply must fit the message budget: no partial transfer on the NJ
        o = Route(vars, "4C" + Path("samples") + "0300", maxReply: 10);
        Assert.Equal(("CC001100", "11"), (Convert.ToHexString(o.Reply), o.Error));
    }

    [Fact]
    public void SimpleDataSegment_ReadsPartOfTheImage()
    {
        var (_, vars) = Fixture();
        vars.WriteAll("samples", new[] { 1, -1, 300 });
        vars.Write("label", "hello");
        vars.WriteAll("flags", new[] { true, false, true, true });
        var o = Route(vars, "4C" + Path("samples", segment: (2, 4)) + "0100");
        Assert.Equal("CC000000" + "C300" + "FFFF2C01", Convert.ToHexString(o.Reply));
        Assert.Equal(("read", "samples", 4, null), (o.Op, o.Target, o.Points, o.Error));
        // a string image is `length` bytes, NUL padded; each chunk carries its own byte count
        Assert.Equal("CC000000" + "D000" + "0800" + "68656C6C6F000000", Convert.ToHexString(Route(vars, "4C" + Path("label", segment: (0, 8)) + "0100").Reply));
        Assert.Equal("CC000000" + "D000" + "0400" + "6F000000", Convert.ToHexString(Route(vars, "4C" + Path("label", segment: (4, 4)) + "0100").Reply));
        // a BOOL array image is the packed words
        Assert.Equal("CC000000" + "C100" + "0D00", Convert.ToHexString(Route(vars, "4C" + Path("flags", segment: (0, 2)) + "0100").Reply));
        // beyond the image: 1F with 0104,1103 (segment boundary)
        o = Route(vars, "4C" + Path("label", segment: (6, 4)) + "0100");
        Assert.Equal(("CC001F02" + "0401" + "0311", "1F:0104,1103"), (Convert.ToHexString(o.Reply), o.Error));
        Assert.Equal("CC002001" + "1880", Convert.ToHexString(Route(vars, "4C" + Path("label", segment: (0, 0)) + "0100").Reply));
        // an element index and a data segment together are not a valid path
        Assert.Equal("CC000400", Convert.ToHexString(Route(vars, "4C" + Path("samples", 1, (0, 2)) + "0100").Reply));
    }

    // --- tags: write -----------------------------------------------------------------------------------

    [Fact]
    public void WriteTag_Scalar_Array_Bool_String_Time()
    {
        var (_, vars) = Fixture();
        var o = Route(vars, "4D" + Path("counter") + "C400" + "0100" + "07000000");
        Assert.Equal("CD000000", Convert.ToHexString(o.Reply));
        Assert.Equal(("write", "counter", 1, null), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal(7, vars.Read<int>("counter"));

        vars.WriteAll("samples", new[] { 1, -1, 300 });
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("samples", 1) + "C300" + "0200" + "05000600").Reply));
        Assert.Equal(new[] { 1, 5, 6 }, vars.ReadAll<int>("samples"));

        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("pump") + "C100" + "0100" + "0100").Reply));
        Assert.True(vars.Read<bool>("pump"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("pump") + "C100" + "0100" + "0000").Reply));
        Assert.False(vars.Read<bool>("pump"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("flags") + "C100" + "0400" + "0A00").Reply));
        Assert.Equal(new[] { false, true, false, true }, vars.ReadAll<bool>("flags"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("flags", 1) + "C100" + "0200" + "0101").Reply));
        Assert.Equal(new[] { false, true, true, true }, vars.ReadAll<bool>("flags"));

        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("label") + "D000" + "0100" + "0300" + "616263").Reply));
        Assert.Equal("abc", vars.Read<string>("label"));

        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("dur") + "0900" + "0100" + "0094357700000000").Reply));
        Assert.Equal(2000, vars.Read<int>("dur"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("stamp") + "0A00" + "0100" + "00CA9A3B00000000").Reply));
        Assert.Equal(1UL, vars.Read<ulong>("stamp"));

        // simple data segment writes replace part of the image
        vars.Write("label", "hello");
        o = Route(vars, "4D" + Path("label", segment: (0, 3)) + "D000" + "0100" + "0300" + "78797A");
        Assert.Equal(("CD000000", "write", "label", 3), (Convert.ToHexString(o.Reply), o.Op, o.Target, o.Points));
        Assert.Equal("xyzlo", vars.Read<string>("label"));
        Assert.Equal("CD000000", Convert.ToHexString(Route(vars, "4D" + Path("samples", segment: (2, 2)) + "C300" + "0100" + "0900").Reply));
        Assert.Equal(new[] { 1, 9, 6 }, vars.ReadAll<int>("samples"));
    }

    [Theory]
    [InlineData("4D{counter}C3000100" + "07000000", "CD002001" + "2280", "20:8022", "data type does not match the tag")]
    [InlineData("4D{counter}C4010100" + "07000000", "CD002001" + "2180", "20:8021", "AddInfo length other than 0 or 2")]
    [InlineData("4D{samples}C3000000", "CD002001" + "1880", "20:8018", "zero elements")]
    [InlineData("4D{counter}C4000200" + "0700000007000000", "CD002001" + "1780", "20:8017", "two elements of a scalar")]
    [InlineData("4D{ro}C3000100" + "0100", "CD001F02" + "0201" + "0321", "1F:0102,2103", "read-only variable")]
    [InlineData("4D{counter}C4000100" + "0700", "CD001300", "13", "too little data")]
    [InlineData("4D{counter}C4000100" + "0700000000", "CD001500", "15", "too much data")]
    [InlineData("4D{pump}C1000100" + "01", "CD001300", "13", "a BOOL needs status + forced")]
    [InlineData("4D{nothere}C4000100" + "07000000", "CD000400", "04", "unknown tag")]
    [InlineData("4D{label}D0000100" + "0900" + "616263646566676869", "CD001500", "15", "string longer than the variable")]
    [InlineData("4D{label}D0000100" + "0300" + "6162", "CD001300", "13", "string count larger than the bytes")]
    public void WriteTag_Errors(string requestTemplate, string reply, string error, string why)
    {
        var (_, vars) = Fixture();
        vars.Write("counter", 1);
        var request = requestTemplate
            .Replace("{counter}", Path("counter")).Replace("{samples}", Path("samples")).Replace("{ro}", Path("ro"))
            .Replace("{pump}", Path("pump")).Replace("{nothere}", Path("nothere")).Replace("{label}", Path("label"));
        var o = Route(vars, request);
        Assert.True(reply == Convert.ToHexString(o.Reply), $"{why}: expected {reply} got {Convert.ToHexString(o.Reply)}");
        Assert.Equal(error, o.Error);
        Assert.Equal(1, vars.Read<int>("counter"));
    }

    [Fact]
    public void UnsupportedServices_And_Objects()
    {
        var (_, vars) = Fixture();
        // no fragmented services, no Read Modify Write on the NJ
        var o = Route(vars, "52" + Path("samples") + "0100" + "00000000");
        Assert.Equal(("D2000800", "08"), (Convert.ToHexString(o.Reply), o.Error));
        Assert.Equal("CE000800", Convert.ToHexString(Route(vars, "4E" + Path("flags") + "0100").Reply));
        // no Message Router (Multiple Service Packet), no Logix program name object
        Assert.Equal("8A000500", Convert.ToHexString(Route(vars, "0A" + "02" + "2002" + "2401" + "0000").Reply));
        Assert.Equal("81000500", Convert.ToHexString(Route(vars, "01" + "02" + "2064" + "2401").Reply));
        // known Omron objects with another service
        Assert.Equal("D5000800", Convert.ToHexString(Route(vars, "55" + "03" + "206B" + "25000000" + "0100" + "0100").Reply));
    }

    // --- objects ----------------------------------------------------------------------------------------

    [Fact]
    public void Identity_IsAnNj501()
    {
        var (_, vars) = Fixture();
        var o = Route(vars, "01" + "02" + "2001" + "2401");
        Assert.Equal("81000000" + "2F00" + "0C00" + "6506" + "0206" + "0000" + "5A4F4D00" + "0A" + Hex("NJ501-1300"), Convert.ToHexString(o.Reply));
        Assert.Equal(("identity", "01", null), (o.Op, o.Target, o.Error));
        // class attributes: revision 1, max instance 1
        Assert.Equal("81000000" + "0100" + "0100", Convert.ToHexString(Route(vars, "01" + "02" + "2001" + "2400").Reply));
        Assert.Equal("8E000000" + "2F00", Convert.ToHexString(Route(vars, "0E" + "03" + "2001" + "2401" + "3001").Reply));
        Assert.Equal("8E000000" + "0A" + Hex("NJ501-1300"), Convert.ToHexString(Route(vars, "0E" + "03" + "2001" + "2401" + "3007").Reply));
        Assert.Equal("8E001400", Convert.ToHexString(Route(vars, "0E" + "03" + "2001" + "2401" + "3009").Reply));
    }

    [Fact]
    public void TagNameServer_ListsUserVariables()
    {
        var (_, vars) = Fixture();
        // Get Instance List (5F): start id, count, kind (2 = user)
        var o = Route(vars, "5F" + "03" + "206A" + "25000000" + "01000000" + "64000000" + "0200");
        var hex = Convert.ToHexString(o.Reply);
        var entry1 = "01000000" + "0C00" + "6B00" + "01000000" + "04" + Hex("temp") + "00";
        var entry2 = "02000000" + "0E00" + "6B00" + "02000000" + "07" + Hex("counter");
        Assert.StartsWith("DF000000" + "0A00" + "00" + "00" + entry1 + entry2, hex);
        Assert.Equal(("tags", "from=1", 10, null), (o.Op, o.Target, o.Points, o.Error));
        // from 9: the last two, nothing more
        o = Route(vars, "5F" + "03" + "206A" + "25000000" + "09000000" + "64000000" + "0200");
        Assert.StartsWith("DF000000" + "0200" + "00" + "00" + "09000000", Convert.ToHexString(o.Reply));
        Assert.Equal(2, o.Points);
        // from 3, two at a time: more remain
        o = Route(vars, "5F" + "03" + "206A" + "25000000" + "03000000" + "02000000" + "0200");
        Assert.StartsWith("DF000000" + "0200" + "01" + "00" + "03000000", Convert.ToHexString(o.Reply));
        Assert.Contains("04000000" + "0E00" + "6B00" + "04000000" + "07" + Hex("samples"), Convert.ToHexString(o.Reply));
        // the reply budget cuts the list: the client continues from the next id
        o = Route(vars, "5F" + "03" + "206A" + "25000000" + "01000000" + "64000000" + "0200", maxReply: 40);
        Assert.Equal("DF000000" + "0100" + "01" + "00" + entry1, Convert.ToHexString(o.Reply));
        // system variables: none
        Assert.Equal("DF000000" + "0000" + "00" + "00", Convert.ToHexString(Route(vars, "5F" + "03" + "206A" + "25000000" + "01000000" + "64000000" + "0100").Reply));
        Assert.Equal("DF002000", Convert.ToHexString(Route(vars, "5F" + "03" + "206A" + "25000000" + "01000000" + "64000000" + "0300").Reply));
        Assert.Equal("DF001300", Convert.ToHexString(Route(vars, "5F" + "03" + "206A" + "25000000" + "01000000").Reply));
        // class attributes of the Tag Name Server: revision, max instance, number of instances
        Assert.Equal("81000000" + "0100" + "0A00" + "0A00", Convert.ToHexString(Route(vars, "01" + "03" + "206A" + "25000000").Reply));
        Assert.Equal("8E000800", Convert.ToHexString(Route(vars, "0E" + "03" + "206A" + "25000000").Reply));
    }

    [Fact]
    public void VariableObject_DescribesTags()
    {
        var (_, vars) = Fixture();
        const string Unknown8 = "0000000000000000";
        // Get Attributes All on the symbolic path (aphytcomm) or on class 6B instance n
        var o = Route(vars, "01" + Path("samples"));
        Assert.Equal("81000000" + "02000000" + "A3" + "C3" + "01" + "00" + "03000000" + Unknown8 + "00" + "000000" + "00000000" + "00000000", Convert.ToHexString(o.Reply));
        Assert.Equal(("variable", "samples", 1, null), (o.Op, o.Target, o.Points, o.Error));
        Assert.Equal("81000000" + "04000000" + "CA" + "00" + "00" + "00" + Unknown8 + "00" + "000000" + "00000000", Convert.ToHexString(Route(vars, "01" + Path("temp")).Reply));
        Assert.Equal("81000000" + "02000000" + "C1" + "00" + "00" + "00" + Unknown8 + "00" + "000000" + "00000000", Convert.ToHexString(Route(vars, "01" + Path("pump")).Reply));
        Assert.Equal("81000000" + "02000000" + "A3" + "C1" + "01" + "00" + "04000000" + Unknown8 + "00" + "000000" + "00000000" + "00000000", Convert.ToHexString(Route(vars, "01" + Path("flags")).Reply));
        // a STRING refers to its Variable Type Object instance (both where aphytcomm looks)
        Assert.Equal("81000000" + "08000000" + "D0" + "00" + "00" + "00" + "01000000" + "00000000" + "00" + "000000" + "01000000", Convert.ToHexString(Route(vars, "01" + Path("label")).Reply));
        o = Route(vars, "01" + "03" + "206B" + "25000200");
        Assert.Equal("81000000" + "04000000" + "C4" + "00" + "00" + "00" + Unknown8 + "00" + "000000" + "00000000", Convert.ToHexString(o.Reply));
        Assert.Equal(("variable", "counter"), (o.Op, o.Target));
        Assert.Equal("81000000" + "0100" + "0A00" + "0A00", Convert.ToHexString(Route(vars, "01" + "03" + "206B" + "25000000").Reply));
        Assert.Equal("81000500", Convert.ToHexString(Route(vars, "01" + "03" + "206B" + "25006300").Reply));
        Assert.Equal("81000400", Convert.ToHexString(Route(vars, "01" + Path("nothere")).Reply));
        // Variable Type Objects: one per distinct STRING length
        Assert.Equal("81000000" + "0100" + "0100" + "0100", Convert.ToHexString(Route(vars, "01" + "03" + "206C" + "25000000").Reply));
        o = Route(vars, "01" + "03" + "206C" + "25000100");
        Assert.Equal("81000000" + "08000000" + "00" + "D0" + "00" + "00" + "0000" + "00000000" + "0000" + "09" + Hex("STRING[8]") + "00000000" + "00000000", Convert.ToHexString(o.Reply));
        Assert.Equal(("type", "1"), (o.Op, o.Target));
        Assert.Equal("81000500", Convert.ToHexString(Route(vars, "01" + "03" + "206C" + "25000200").Reply));
    }

    [Fact]
    public void ForwardOpen_LimitedTo1994_And_UnconnectedSend()
    {
        var (_, vars) = Fixture();
        var connections = new FakeConnections();
        const string Head = "5B" + "02" + "2006" + "2401" + "0A" + "05" + "00000000" + "78563412" + "0100" + "3412" + "11223344" + "07" + "000000" + "01402000";
        const string Tail = "01402000" + "F4010042" + "A3" + "03" + "010020022401";
        var o = Route(vars, Head + "CA070042" + Tail, connections);
        Assert.Equal("DB000000", Convert.ToHexString(o.Reply[..4]));
        Assert.Equal(("forward-open", "size=1994"), (o.Op, o.Target));
        Assert.Equal(1994, connections.Open[0].Size);
        o = Route(vars, Head + "CB070042" + Tail, connections);
        Assert.Equal(("DB000101" + "0901", "01:0109"), (Convert.ToHexString(o.Reply), o.Error));
        Assert.Single(connections.Open);
        // forward close
        Assert.Equal("CE000000" + "0100" + "3412" + "11223344" + "0000", Convert.ToHexString(Route(vars, "4E" + "02" + "2006" + "2401" + "0A05" + "0100" + "3412" + "11223344" + "03" + "00" + "010020022401", connections).Reply));
        Assert.Empty(connections.Open);
        // unconnected send unwraps the embedded request
        vars.Write("counter", 42);
        var embedded = "4C" + Path("counter") + "0100";
        var o2 = Route(vars, "52" + "02" + "2006" + "2401" + "0A05" + $"{embedded.Length / 2:X2}00" + embedded + "01" + "00" + "0100");
        Assert.Equal("CC000000" + "C400" + "2A000000", Convert.ToHexString(o2.Reply));
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
        cpf.AddRange([0, 0, 0, 0, 0, 0]);
        cpf.AddRange([2, 0, 0, 0, 0, 0]);
        cpf.AddRange([0xB2, 0, (byte)cip.Length, (byte)(cip.Length >> 8)]);
        cpf.AddRange(cip);
        return Encap.Build(Encap.SendRRData, session, 0, new byte[8], cpf.ToArray());
    }

    [Fact]
    public async Task Tcp_NjDialect_ListIdentity_And_UnconnectedRead()
    {
        var (_, vars) = Fixture();
        vars.Write("label", "hi");
        var provider = new CapturingProvider();
        var server = new EnipServer("nj-srv", new IPEndPoint(IPAddress.Loopback, 0), vars, CipDialect.OmronNj, provider.CreateLogger("srv"));
        using var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        await server.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await ExpectLogAsync(provider, e => e.Message.Contains("enip-nj"));

        using var client = new TcpClient();
        await client.ConnectAsync(server.BoundEndpoint!);
        var stream = client.GetStream();

        var identity = await ExchangeAsync(stream, Encap.Build(Encap.ListIdentity, 0, 0, new byte[8], []));
        var hex = Convert.ToHexString(identity);
        Assert.Contains(Hex("NJ501-1300"), hex);
        Assert.Contains("2F00" + "0C00" + "6506", hex);

        var reg = await ExchangeAsync(stream, Encap.Build(Encap.RegisterSession, 0, 0, new byte[8], Convert.FromHexString("01000000")));
        Assert.True(Encap.TryParseHeader(reg, out _, out _, out var session, out _));
        var rr = await ExchangeAsync(stream, SendRRData(session, Convert.FromHexString("4C" + Path("label") + "0100")));
        Assert.EndsWith("B200" + "0A00" + "CC000000" + "D000" + "0200" + "6869", Convert.ToHexString(rr));
        await ExpectLogAsync(provider, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("target=label") && e.Message.Contains("result=ok"));
        cts.Cancel();
    }
}
