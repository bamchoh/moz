using System.Buffers.Binary;
using System.Text;
using Moz.Devices;
using static Moz.EtherNetIp.CipCommon;

namespace Moz.EtherNetIp;

/// <summary>
/// CIP message router of an Omron NJ / NX-style tag server (W506 chapter 7,
/// "CIP Message Communications"): Read / Write variable (4C / 4D) on
/// symbolic paths with Omron's data formats (one BOOL = status + forced
/// byte, a whole BOOL array = bits packed into words, STRING = UINT byte
/// count + UTF-8, TIME / DATE types = 8-byte nanosecond counts with the
/// vendor codes 08..0B), the Simple Data Segment for partial access
/// (aphytcomm reads strings and arrays that way), Identity (01), the Tag
/// Name Server (6A, Get Instance List 5F), the Variable Object (6B) and the
/// Variable Type Object (6C) that aphytcomm / libplctag browse with, and
/// Connection Manager Forward Open / Close (connection size ≤ 1994) and
/// Unconnected Send. No fragmented services, no Multiple Service Packet,
/// no structures (moz expands struct variables into leaves).
/// </summary>
public static class NjCipRouter
{
    /// <summary>Largest class 3 connection (Large Forward Open) of an NJ CPU (W506 7-1-2).</summary>
    public const int ConnectionSizeMax = 1994;

    // Omron general / additional status codes (W506 7-6-1, 7-6-2)
    private const byte ReplyDataTooLarge = 0x11;
    private const byte VendorSpecific = 0x1F;
    private const ushort NoElements = 0x8017;          // more than one element of a variable without elements
    private const ushort ElementRange = 0x8018;        // zero elements or beyond the array
    private const ushort BadAddInfoLength = 0x8021;    // AddInfo length other than 0 or 2
    private const ushort TypeMismatch = 0x8022;        // the data type does not agree with the tag
    private const ushort BadLengthInfo = 0x800F;       // inconsistent data length information
    private const ushort OutOfRangeValue = 0x8028;
    private static readonly ushort[] ReadOnlyVariable = [0x0102, 0x2103];
    private static readonly ushort[] SegmentBoundary = [0x0104, 0x1103];

    public static CipOutcome Handle(ReadOnlySpan<byte> request, CipContext ctx)
    {
        if (request.Length < 2)
        {
            return Fail(0, PathSegmentError, "cmd=??", "?");
        }
        byte service = request[0];
        int pathBytes = request[1] * 2;
        if (2 + pathBytes > request.Length)
        {
            return Fail(service, PathSegmentError, $"cmd={service:X2}", "?");
        }
        var path = CipPath.Parse(request.Slice(2, pathBytes));
        var data = request[(2 + pathBytes)..];
        if (path.Symbol is not null)
        {
            return Tag(service, path, data, ctx);
        }
        if (!path.Valid)
        {
            return Fail(service, PathSegmentError, $"cmd={service:X2}", "?");
        }
        return path.Class switch
        {
            0x01 => Identity(service, path, ctx),
            0x06 => ConnectionManager(service, data, ctx),
            0x6A => TagNameServer(service, data, ctx),
            0x6B => service == 0x01 ? VariableObject(path, ctx) : Fail(service, ServiceNotSupported, $"cmd={service:X2}", "variable"),
            0x6C => service == 0x01 ? VariableTypeObject(path, ctx) : Fail(service, ServiceNotSupported, $"cmd={service:X2}", "type"),
            _ => Fail(service, PathDestinationUnknown, $"cmd={service:X2}", $"class={path.Class:X2}"),
        };
    }

    // --- variables --------------------------------------------------------------------------------------

    private static CipOutcome Tag(byte service, CipPath path, ReadOnlySpan<byte> data, CipContext ctx)
    {
        var name = path.Symbol!;
        var op = service switch { 0x4C => "read", 0x4D => "write", 0x01 => "variable", _ => $"cmd={service:X2}" };
        if (service is not (0x4C or 0x4D or 0x01))
        {
            return Fail(service, ServiceNotSupported, op, name);
        }
        if (!TagInfo.TryFind(ctx.Table, name, out var info))
        {
            return Fail(service, PathSegmentError, op, name);
        }
        var tag = new NjTag(info.Variable);
        if (service == 0x01)
        {
            return new CipOutcome(Reply(service, Ok, VariableAttributes(tag, ctx)), op, name, 1, null);
        }
        if (path.DataSize is { } size)
        {
            if (path.Element is not null)
            {
                return Fail(service, PathSegmentError, op, name);
            }
            return service == 0x4C
                ? ReadSegment(tag, path.DataOffset!.Value, size, data, ctx)
                : WriteSegment(tag, path.DataOffset!.Value, size, data, ctx);
        }
        return service == 0x4C ? Read(tag, path.Element, data, ctx) : Write(tag, path.Element, data, ctx);
    }

    /// <summary>Read (4C): Num of Element (UINT) → data type, AddInfo length 0, the data.</summary>
    private static CipOutcome Read(NjTag tag, int? element, ReadOnlySpan<byte> data, CipContext ctx)
    {
        const byte service = 0x4C;
        const string op = "read";
        if (data.Length < 2)
        {
            return Fail(service, NotEnoughData, op, tag.Name);
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        byte[] bytes;
        if (tag.Count == 1)
        {
            if (count != 1 || element is not null)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, NoElements);
            }
            bytes = tag.Elementary(ctx.Table, 0);
        }
        else if (element is { } index)
        {
            if (count == 0 || index + count > tag.Count)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, ElementRange);
            }
            bytes = tag.IsBool && count == 1 ? tag.Elementary(ctx.Table, index) : tag.Range(ctx.Table, index, count);
        }
        else if (tag.IsBool)
        {
            // the whole BOOL array: packed words (Num of Element 1 or the array length)
            if (count != 1 && count != tag.Count)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, ElementRange);
            }
            bytes = tag.Image(ctx.Table);
        }
        else
        {
            if (count == 0 || count > tag.Count)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, ElementRange);
            }
            bytes = tag.Range(ctx.Table, 0, count);
        }
        return Deliver(service, op, tag, count, [.. tag.TypeBytes, .. bytes], ctx);
    }

    /// <summary>Read through a Simple Data Segment: bytes offset..offset+size of the variable's image (a STRING chunk carries its own byte count).</summary>
    private static CipOutcome ReadSegment(NjTag tag, int offset, int size, ReadOnlySpan<byte> data, CipContext ctx)
    {
        const byte service = 0x4C;
        const string op = "read";
        if (data.Length < 2)
        {
            return Fail(service, NotEnoughData, op, tag.Name);
        }
        if (size == 0)
        {
            return Fail(service, InvalidParameter, op, tag.Name, 0, ElementRange);
        }
        if (offset < 0 || offset + size > tag.ImageSize)
        {
            return Fail(service, VendorSpecific, op, tag.Name, size, SegmentBoundary);
        }
        var chunk = tag.Image(ctx.Table).AsSpan(offset, size);
        byte[] body = tag.IsString ? [.. tag.TypeBytes, .. U16((ushort)size), .. chunk] : [.. tag.TypeBytes, .. chunk];
        return Deliver(service, op, tag, size, body, ctx);
    }

    private static CipOutcome Deliver(byte service, string op, NjTag tag, int points, byte[] body, CipContext ctx)
    {
        if (4 + body.Length > ctx.MaxReply)
        {
            return Fail(service, ReplyDataTooLarge, op, tag.Name, points);
        }
        return new CipOutcome(Reply(service, Ok, body), op, tag.Name, points, null);
    }

    /// <summary>Write (4D): data type, AddInfo length (0), Num of Element (UINT), the data.</summary>
    private static CipOutcome Write(NjTag tag, int? element, ReadOnlySpan<byte> data, CipContext ctx)
    {
        const byte service = 0x4D;
        const string op = "write";
        if (CheckWriteHeader(tag, data, op) is { } bad)
        {
            return bad;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var payload = data[4..];
        int index = 0;
        int expected;
        Action<VariableTable, byte[]> apply;
        if (tag.IsString)
        {
            // one STRING at a time: UINT byte count + the bytes, within the variable's length
            if (count != 1 || (tag.Count == 1 ? element is not null : element is null))
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, tag.Count == 1 ? NoElements : ElementRange);
            }
            index = element ?? 0;
            if (index >= tag.Count)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, ElementRange);
            }
            if (payload.Length < 2)
            {
                return Fail(service, NotEnoughData, op, tag.Name, count);
            }
            int length = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            if (length > tag.Variable.Length)
            {
                return Fail(service, TooMuchData, op, tag.Name, count);
            }
            expected = 2 + length;
            apply = (t, b) => tag.PutElementary(t, index, b);
        }
        else if (tag.Count == 1)
        {
            if (count != 1 || element is not null)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, NoElements);
            }
            expected = tag.ElementarySize;
            apply = (t, b) => tag.PutElementary(t, 0, b);
        }
        else if (element is { } first)
        {
            if (count == 0 || first + count > tag.Count)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, ElementRange);
            }
            index = first;
            if (tag.IsBool && count == 1)
            {
                expected = tag.ElementarySize;
                apply = (t, b) => tag.PutElementary(t, index, b);
            }
            else
            {
                expected = count * tag.ElementSize;
                apply = (t, b) => tag.PutRange(t, index, b);
            }
        }
        else if (tag.IsBool)
        {
            if (count != 1 && count != tag.Count)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, ElementRange);
            }
            expected = tag.ImageSize;
            apply = (t, b) => tag.PutImage(t, b);
        }
        else
        {
            if (count == 0 || count > tag.Count)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count, ElementRange);
            }
            expected = count * tag.ElementSize;
            apply = (t, b) => tag.PutRange(t, 0, b);
        }
        return Commit(service, op, tag, count, payload, expected, apply, ctx);
    }

    /// <summary>Write through a Simple Data Segment: replace bytes offset..offset+size of the image (a STRING chunk carries its own byte count).</summary>
    private static CipOutcome WriteSegment(NjTag tag, int offset, int size, ReadOnlySpan<byte> data, CipContext ctx)
    {
        const byte service = 0x4D;
        const string op = "write";
        if (CheckWriteHeader(tag, data, op) is { } bad)
        {
            return bad;
        }
        var payload = data[4..];
        if (size == 0)
        {
            return Fail(service, InvalidParameter, op, tag.Name, 0, ElementRange);
        }
        if (offset < 0 || offset + size > tag.ImageSize)
        {
            return Fail(service, VendorSpecific, op, tag.Name, size, SegmentBoundary);
        }
        if (tag.IsString)
        {
            if (payload.Length < 2)
            {
                return Fail(service, NotEnoughData, op, tag.Name, size);
            }
            if (BinaryPrimitives.ReadUInt16LittleEndian(payload) != size)
            {
                return Fail(service, InvalidParameter, op, tag.Name, size, BadLengthInfo);
            }
            payload = payload[2..];
        }
        return Commit(service, op, tag, size, payload, size, (t, b) =>
        {
            var image = tag.Image(t);
            b.CopyTo(image, offset);
            tag.PutImage(t, image);
        }, ctx);
    }

    /// <summary>Data type + AddInfo length of a write request: 13 when short, 20 + 8021 / 8022 when not this tag's elementary type.</summary>
    private static CipOutcome? CheckWriteHeader(NjTag tag, ReadOnlySpan<byte> data, string op)
    {
        if (data.Length < 4)
        {
            return Fail(0x4D, NotEnoughData, op, tag.Name);
        }
        if (data[1] is not (0 or 2))
        {
            return Fail(0x4D, InvalidParameter, op, tag.Name, 0, BadAddInfoLength);
        }
        if (data[0] != tag.TypeCode || data[1] != 0)
        {
            return Fail(0x4D, InvalidParameter, op, tag.Name, 0, TypeMismatch);
        }
        return null;
    }

    private static CipOutcome Commit(byte service, string op, NjTag tag, int points, ReadOnlySpan<byte> payload, int expected, Action<VariableTable, byte[]> apply, CipContext ctx)
    {
        if (payload.Length < expected)
        {
            return Fail(service, NotEnoughData, op, tag.Name, points);
        }
        if (payload.Length > expected)
        {
            return Fail(service, TooMuchData, op, tag.Name, points);
        }
        if (tag.Variable.Table.IsReadOnly())
        {
            return Fail(service, VendorSpecific, op, tag.Name, points, ReadOnlyVariable);
        }
        try
        {
            apply(ctx.Table, payload.ToArray());
        }
        catch (ArgumentException)
        {
            return Fail(service, InvalidParameter, op, tag.Name, points, OutOfRangeValue);
        }
        return new CipOutcome(Reply(service, Ok, []), op, tag.Name, points, null);
    }

    // --- objects ----------------------------------------------------------------------------------------

    private static CipOutcome ConnectionManager(byte service, ReadOnlySpan<byte> data, CipContext ctx)
    {
        switch (service)
        {
            case 0x52:
                return TryEmbedded(data, out var embedded) ? Handle(embedded, ctx) : Fail(service, NotEnoughData, "unconnected-send", "?");

            case 0x54:
            case 0x5B:
                return ForwardOpen(service, data, ctx, ConnectionSizeMax);

            case 0x4E:
                return ForwardClose(data, ctx);

            default:
                return Fail(service, ServiceNotSupported, $"cmd={service:X2}", "connection-manager");
        }
    }

    /// <summary>Class attributes of the variable objects: revision 1, max instance, number of instances.</summary>
    private static byte[] ClassAttributes(int instances) => [.. U16(1), .. U16((ushort)instances), .. U16((ushort)instances)];

    /// <summary>
    /// Tag Name Server (6A): Get Attributes All = class attributes; Get Instance
    /// List (5F; start id UDINT, count UDINT, kind UINT 1 = system / 2 = user)
    /// = count (UINT), more flag, pad, then per tag: id, entry length, class
    /// 6B, id, name length, UTF-8 name, pad to even. Tags are the variables in
    /// order (ids 1..n), all "user"; the reply budget may cut the list (more = 1).
    /// </summary>
    private static CipOutcome TagNameServer(byte service, ReadOnlySpan<byte> data, CipContext ctx)
    {
        const string op = "tags";
        if (service == 0x01)
        {
            return new CipOutcome(Reply(service, Ok, ClassAttributes(ctx.Table.Variables.Count)), op, "01", 0, null);
        }
        if (service != 0x5F)
        {
            return Fail(service, ServiceNotSupported, $"cmd={service:X2}", op);
        }
        if (data.Length < 10)
        {
            return Fail(service, NotEnoughData, op, "?");
        }
        uint start = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint want = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int kind = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var target = $"from={start}";
        if (kind is not (1 or 2))
        {
            return Fail(service, InvalidParameter, op, target);
        }
        var body = new List<byte>();
        int returned = 0;
        bool more = false;
        if (kind == 2)
        {
            var variables = ctx.Table.Variables;
            int budget = ctx.MaxReply - 4 - 4;
            long id = Math.Max(start, 1);
            for (; id <= variables.Count && returned < want; id++)
            {
                var name = Encoding.UTF8.GetBytes(variables[(int)id - 1].Name);
                int length = 2 + 4 + 1 + name.Length;
                int pad = length % 2;
                byte[] entry = [.. U32((uint)id), .. U16((ushort)(length + pad)), .. U16(0x6B), .. U32((uint)id), (byte)name.Length, .. name, .. new byte[pad]];
                if (body.Count + entry.Length > budget)
                {
                    break;
                }
                body.AddRange(entry);
                returned++;
            }
            more = id <= variables.Count;
        }
        byte[] reply = [.. U16((ushort)returned), (byte)(more ? 1 : 0), 0, .. body];
        return new CipOutcome(Reply(service, Ok, reply), op, target, returned, null);
    }

    /// <summary>Variable Object (6B): Get Attributes All of instance 0 = class attributes, of instance n = the n-th variable (see <see cref="VariableAttributes"/>).</summary>
    private static CipOutcome VariableObject(CipPath path, CipContext ctx)
    {
        const string op = "variable";
        var variables = ctx.Table.Variables;
        if (path.Instance <= 0)
        {
            return new CipOutcome(Reply(0x01, Ok, ClassAttributes(variables.Count)), op, "0", 0, null);
        }
        if (path.Instance > variables.Count)
        {
            return Fail(0x01, PathDestinationUnknown, op, path.Instance.ToString());
        }
        var tag = new NjTag(variables[path.Instance - 1]);
        return new CipOutcome(Reply(0x01, Ok, VariableAttributes(tag, ctx)), op, tag.Name, 1, null);
    }

    /// <summary>
    /// The Variable Object attributes as aphytcomm / libplctag read them: size
    /// (UDINT; the element size of an array), data type (A3 for an array, then
    /// the element type, the dimension count 1 and a pad), the dimension sizes
    /// (UDINT each), 8 bytes (the Variable Type Object instance of a STRING,
    /// then 0), bit number (USINT) + 3 pad, the Variable Type Object instance
    /// (UDINT), and the start index of each dimension.
    /// </summary>
    private static byte[] VariableAttributes(NjTag tag, CipContext ctx)
    {
        uint typeInstance = tag.IsString ? (uint)(StringTypes(ctx).IndexOf(tag.Variable.Length) + 1) : 0;
        var body = new List<byte>(U32((uint)tag.MemorySize));
        if (tag.Count > 1)
        {
            body.AddRange([0xA3, tag.TypeCode, 1, 0]);
            body.AddRange(U32((uint)tag.Count));
        }
        else
        {
            body.AddRange([tag.TypeCode, 0, 0, 0]);
        }
        body.AddRange(U32(typeInstance));
        body.AddRange(U32(0));
        body.AddRange([0, 0, 0, 0]);
        body.AddRange(U32(typeInstance));
        if (tag.Count > 1)
        {
            body.AddRange(U32(0));
        }
        return body.ToArray();
    }

    /// <summary>The distinct STRING lengths of the tags, in first-appearance order: Variable Type Object instances 1..k.</summary>
    private static List<int> StringTypes(CipContext ctx)
    {
        var lengths = new List<int>();
        foreach (var v in ctx.Table.Variables)
        {
            if (v.Type == VariableType.String && !lengths.Contains(v.Length))
            {
                lengths.Add(v.Length);
            }
        }
        return lengths;
    }

    /// <summary>
    /// Variable Type Object (6C): instance 0 = class attributes; instance k =
    /// the k-th STRING length as a derived type: size (UDINT), 0, data type D0,
    /// 0, dimensions 0, member count (UINT) 0, 4 bytes 0, CRC (UINT) 0, name
    /// length + "STRING[n]" padded to even, next instance 0, nesting instance 0.
    /// </summary>
    private static CipOutcome VariableTypeObject(CipPath path, CipContext ctx)
    {
        const string op = "type";
        var types = StringTypes(ctx);
        if (path.Instance <= 0)
        {
            return new CipOutcome(Reply(0x01, Ok, ClassAttributes(types.Count)), op, "0", 0, null);
        }
        if (path.Instance > types.Count)
        {
            return Fail(0x01, PathDestinationUnknown, op, path.Instance.ToString());
        }
        int length = types[path.Instance - 1];
        var name = Encoding.ASCII.GetBytes($"STRING[{length}]");
        int pad = (1 + name.Length) % 2;
        byte[] body =
        [
            .. U32((uint)length), 0, 0xD0, 0, 0,
            .. U16(0), .. U32(0), .. U16(0),
            (byte)name.Length, .. name, .. new byte[pad],
            .. U32(0), .. U32(0),
        ];
        return new CipOutcome(Reply(0x01, Ok, body), op, path.Instance.ToString(), 1, null);
    }
}

/// <summary>A variable seen as an NJ tag: Omron's type code, sizes and the three wire shapes (elementary, range, whole image).</summary>
internal readonly record struct NjTag(Variable Variable)
{
    public string Name => Variable.Name;
    public int Count => Variable.Count;
    public bool IsBool => Variable.Type is VariableType.Bool or VariableType.BoolWord;
    public bool IsString => Variable.Type == VariableType.String;
    private bool IsTime => Variable.Type is VariableType.Time or VariableType.Tod or VariableType.Date or VariableType.Dt;

    private const long NsPerMs = 1_000_000;
    private const ulong NsPerSecond = 1_000_000_000;

    public byte TypeCode => Variable.Type switch
    {
        VariableType.Bool or VariableType.BoolWord => 0xC1,
        VariableType.Int => 0xC3,
        VariableType.Uint => 0xC7,
        VariableType.Dint => 0xC4,
        VariableType.Udint => 0xC8,
        VariableType.Real => 0xCA,
        VariableType.Lint => 0xC5,
        VariableType.Ulint => 0xC9,
        VariableType.String => 0xD0,
        VariableType.Date => 0x08,  // DATE_NSEC
        VariableType.Time => 0x09,  // TIME_NSEC
        VariableType.Dt => 0x0A,    // DATE_AND_TIME_NSEC
        _ => 0x0B,                  // TIME_OF_DAY_NSEC
    };

    /// <summary>Data type + AddInfo length 0 (the prefix of every read reply and write request).</summary>
    public byte[] TypeBytes => [TypeCode, 0];

    /// <summary>Bytes per element in a range / image: BOOL 1 status byte (ranges) — see <see cref="ImageSize"/> for packing, STRING its length, time types 8, numbers their width.</summary>
    public int ElementSize => IsBool ? 1 : IsString ? Variable.Length : IsTime ? 8 : new TagInfo(Variable).ElementSize;

    /// <summary>The Variable Object size attribute: a Sysmac BOOL occupies 16 bits.</summary>
    public int MemorySize => IsBool ? 2 : ElementSize;

    /// <summary>The elementary (single value) format: BOOL status + forced, otherwise one element.</summary>
    public int ElementarySize => IsBool ? 2 : ElementSize;

    /// <summary>The whole variable as bytes: a BOOL array packed 16 per word, STRINGs NUL padded to their length.</summary>
    public int ImageSize => IsBool ? (Count + 15) / 16 * 2 : Count * ElementSize;

    public byte[] Image(VariableTable t)
    {
        if (IsBool)
        {
            var bits = t.ReadAll<bool>(Name);
            var bytes = new byte[ImageSize];
            for (int i = 0; i < bits.Length; i++)
            {
                if (bits[i])
                {
                    bytes[i >> 3] |= (byte)(1 << (i & 7));
                }
            }
            return bytes;
        }
        if (IsString)
        {
            var image = new byte[ImageSize];
            var values = t.ReadAll<string>(Name);
            for (int i = 0; i < values.Length; i++)
            {
                var utf8 = Encoding.UTF8.GetBytes(values[i]);
                utf8.AsSpan(0, Math.Min(utf8.Length, Variable.Length)).CopyTo(image.AsSpan(i * Variable.Length));
            }
            return image;
        }
        return Range(t, 0, Count);
    }

    public void PutImage(VariableTable t, byte[] image)
    {
        if (IsBool)
        {
            var bits = new bool[Count];
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] = ((image[i >> 3] >> (i & 7)) & 1) != 0;
            }
            t.WriteAll(Name, bits);
            return;
        }
        if (IsString)
        {
            var values = new string[Count];
            for (int i = 0; i < values.Length; i++)
            {
                var cell = image.AsSpan(i * Variable.Length, Variable.Length);
                int end = cell.IndexOf((byte)0);
                values[i] = Encoding.UTF8.GetString(end < 0 ? cell : cell[..end]);
            }
            t.WriteAll(Name, values);
            return;
        }
        PutRange(t, 0, image);
    }

    /// <summary>One element in the elementary format: BOOL status + forced (0), STRING UINT byte count + UTF-8, otherwise its bytes.</summary>
    public byte[] Elementary(VariableTable t, int index)
    {
        if (IsBool)
        {
            return [(byte)(t.Read<bool>(Name, index) ? 1 : 0), 0];
        }
        return Range(t, index, 1);
    }

    public void PutElementary(VariableTable t, int index, ReadOnlySpan<byte> data)
    {
        if (IsBool)
        {
            t.Write(Name, data[0] != 0, index);
            return;
        }
        if (IsString)
        {
            t.Write(Name, Encoding.UTF8.GetString(data[2..]), index);
            return;
        }
        PutRange(t, index, data);
    }

    /// <summary>Elements index..index+count: BOOL one status byte each, STRING count + bytes each, time types nanoseconds, numbers little-endian.</summary>
    public byte[] Range(VariableTable t, int index, int count)
    {
        switch (Variable.Type)
        {
            case VariableType.Bool or VariableType.BoolWord:
                return t.ReadAll<bool>(Name).Skip(index).Take(count).Select(b => (byte)(b ? 1 : 0)).ToArray();
            case VariableType.String:
            {
                var body = new List<byte>();
                foreach (var s in t.ReadAll<string>(Name).Skip(index).Take(count))
                {
                    var utf8 = Encoding.UTF8.GetBytes(s);
                    body.AddRange(U16((ushort)utf8.Length));
                    body.AddRange(utf8);
                }
                return body.ToArray();
            }
            case VariableType.Time:
                return Enc(t.ReadAll<int>(Name), index, count, ms => (ulong)(ms * NsPerMs));
            case VariableType.Tod:
                return Enc(t.ReadAll<uint>(Name), index, count, ms => ms * (ulong)NsPerMs);
            case VariableType.Date or VariableType.Dt:
                return Enc(t.ReadAll<ulong>(Name), index, count, s => s * NsPerSecond);
            default:
                return new TagInfo(Variable).Encode(t, index, count);
        }
    }

    public void PutRange(VariableTable t, int index, ReadOnlySpan<byte> data)
    {
        switch (Variable.Type)
        {
            case VariableType.Bool or VariableType.BoolWord:
            {
                var values = new bool[data.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = data[i] != 0;
                }
                Put(t, index, values);
                break;
            }
            case VariableType.Time:
                Put(t, index, Dec(data, ns => (int)((long)ns / NsPerMs)));
                break;
            case VariableType.Tod:
                Put(t, index, Dec(data, ns => (uint)(ns / (ulong)NsPerMs)));
                break;
            case VariableType.Date or VariableType.Dt:
                Put(t, index, Dec(data, ns => ns / NsPerSecond));
                break;
            default:
                new TagInfo(Variable).Decode(t, index, data);
                break;
        }
    }

    private static byte[] Enc<T>(T[] all, int index, int count, Func<T, ulong> ns)
    {
        var bytes = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), ns(all[index + i]));
        }
        return bytes;
    }

    private static T[] Dec<T>(ReadOnlySpan<byte> data, Func<ulong, T> of)
    {
        var values = new T[data.Length / 8];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = of(BinaryPrimitives.ReadUInt64LittleEndian(data[(i * 8)..]));
        }
        return values;
    }

    private void Put<T>(VariableTable t, int index, T[] values)
    {
        if (index == 0 && values.Length == Count)
        {
            t.WriteAll(Name, values);
            return;
        }
        var all = t.ReadAll<T>(Name);
        values.CopyTo(all, index);
        t.WriteAll(Name, all);
    }
}
