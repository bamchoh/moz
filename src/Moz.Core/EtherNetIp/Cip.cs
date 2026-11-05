using System.Buffers.Binary;
using System.Text;
using Moz.Devices;

namespace Moz.EtherNetIp;

/// <summary>
/// The class 3 connections of one originator (TCP connection): Forward Open
/// registers one, Forward Close removes it. Implemented by the transport.
/// </summary>
public interface ICipConnections
{
    /// <summary>Register a connection and return the O→T (originator to target) connection id the originator must use.</summary>
    uint OpenConnection(uint toOriginatorId, ushort serial, ushort vendor, uint originatorSerial, int size);

    /// <summary>Remove the connection identified by the Forward Close triple; false when unknown.</summary>
    bool CloseConnection(ushort serial, ushort vendor, uint originatorSerial);
}

/// <summary>What the router needs: the tags (variables), the identity name, the connection table and the reply budget.</summary>
public sealed record CipContext(VariableTable Table, string Name, ICipConnections Connections, int MaxReply)
{
    public string ProductName => $"moz {Name}";
}

/// <summary>What the router did with one request: the reply plus the log fields (Error = general status hex, "FF:2105" with an extended status).</summary>
public readonly record struct CipOutcome(byte[] Reply, string Op, string Target, int Points, string? Error);

/// <summary>
/// CIP message router of a Logix-style tag server: tags are the device's
/// typed variables (name, type, element count). Services: Read / Write Tag
/// (4C / 4D) and their fragmented forms (52 / 53) on symbolic paths,
/// Multiple Service Packet (0A), Identity (class 01), program name (class
/// 64), Symbol object tag listing (6B / 55), the STRING template (6C),
/// Connection Manager Forward Open / Close and Unconnected Send (06).
/// Pure apart from the device access and the connection table.
/// </summary>
public static class CipRouter
{
    public const ushort VendorId = 0x0000;
    public const ushort DeviceType = 0x000E;    // programmable logic controller
    public const ushort ProductCode = 0x0001;
    public const byte RevisionMajor = 20;       // < 21: clients address tags by name, not instance id
    public const byte RevisionMinor = 1;
    public const uint SerialNumber = 0x004D4F5A; // "MOZ"
    public const ushort StringHandle = 0x0FCE;  // the Logix STRING structure
    public const int StringData = 82;
    public const int StringSize = 4 + StringData + 2;

    // general status codes
    private const byte Ok = 0x00;
    private const byte ConnectionFailure = 0x01;
    private const byte PathSegmentError = 0x04;
    private const byte PathDestinationUnknown = 0x05;
    private const byte PartialTransfer = 0x06;
    private const byte ServiceNotSupported = 0x08;
    private const byte PrivilegeViolation = 0x0F;
    private const byte NotEnoughData = 0x13;
    private const byte AttributeNotSupported = 0x14;
    private const byte TooMuchData = 0x15;
    private const byte InvalidParameter = 0x20;
    private const byte Extended = 0xFF;
    private const ushort BeyondEnd = 0x2105;
    private const ushort TypeMismatch = 0x2107;
    private const ushort ConnectionNotFound = 0x0107;

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
            0x02 => service == 0x0A ? MultipleServicePacket(data, ctx) : Fail(service, ServiceNotSupported, $"cmd={service:X2}", "router"),
            0x06 => ConnectionManager(service, data, ctx),
            0x64 => service == 0x01 ? new CipOutcome(Reply(service, Ok, Str16(ctx.ProductName)), "name", "01", 0, null) : Fail(service, ServiceNotSupported, $"cmd={service:X2}", "name"),
            0x6B => service == 0x55 ? SymbolList(path, data, ctx) : Fail(service, ServiceNotSupported, $"cmd={service:X2}", "symbols"),
            0x6C => Template(service, path, data),
            _ => Fail(service, PathDestinationUnknown, $"cmd={service:X2}", $"class={path.Class:X2}"),
        };
    }

    // --- tags -------------------------------------------------------------------------------------------

    private static CipOutcome Tag(byte service, CipPath path, ReadOnlySpan<byte> data, CipContext ctx)
    {
        var name = path.Symbol!;
        var op = service switch { 0x4C => "read", 0x4D => "write", 0x52 => "read-frag", 0x53 => "write-frag", _ => $"cmd={service:X2}" };
        if (service is not (0x4C or 0x4D or 0x52 or 0x53))
        {
            return Fail(service, ServiceNotSupported, op, name);
        }
        if (!TagInfo.TryFind(ctx.Table, name, out var tag))
        {
            return Fail(service, PathSegmentError, op, name);
        }
        int index = path.Element ?? 0;
        return service switch
        {
            0x4C => ReadTag(tag, index, data, ctx, fragmented: false),
            0x52 => ReadTag(tag, index, data, ctx, fragmented: true),
            0x4D => WriteTag(tag, index, data, ctx, fragmented: false),
            _ => WriteTag(tag, index, data, ctx, fragmented: true),
        };
    }

    private static CipOutcome ReadTag(TagInfo tag, int index, ReadOnlySpan<byte> data, CipContext ctx, bool fragmented)
    {
        byte service = fragmented ? (byte)0x52 : (byte)0x4C;
        var op = fragmented ? "read-frag" : "read";
        int count = data.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : 1;
        int offset = 0;
        if (fragmented)
        {
            if (data.Length < 6)
            {
                return Fail(service, NotEnoughData, op, tag.Name, count);
            }
            offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
        }
        if (count == 0 || index < 0 || index + count > tag.Count)
        {
            return Fail(service, Extended, op, tag.Name, count, BeyondEnd);
        }
        var bytes = tag.Encode(ctx.Table, index, count);
        if (offset > bytes.Length)
        {
            return Fail(service, Extended, op, tag.Name, count, BeyondEnd);
        }
        var type = tag.TypeBytes;
        var remaining = bytes.AsSpan(offset);
        int budget = ctx.MaxReply - 4 - type.Length;
        byte status = Ok;
        if (remaining.Length > budget)
        {
            if (!fragmented)
            {
                return Fail(service, Extended, op, tag.Name, count, BeyondEnd);
            }
            int fit = Math.Max(tag.ElementSize, budget / tag.ElementSize * tag.ElementSize);
            remaining = remaining[..Math.Min(fit, remaining.Length)];
            status = PartialTransfer;
        }
        var reply = Reply(service, status, [.. type, .. remaining]);
        return new CipOutcome(reply, op, tag.Name, count, status == Ok ? null : $"{status:X2}");
    }

    private static CipOutcome WriteTag(TagInfo tag, int index, ReadOnlySpan<byte> data, CipContext ctx, bool fragmented)
    {
        byte service = fragmented ? (byte)0x53 : (byte)0x4D;
        var op = fragmented ? "write-frag" : "write";
        if (data.Length < 4)
        {
            return Fail(service, NotEnoughData, op, tag.Name);
        }
        var type = tag.TypeBytes;
        if (data.Length < type.Length + 2 || !data[..type.Length].SequenceEqual(type))
        {
            return Fail(service, Extended, op, tag.Name, 0, TypeMismatch);
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data[type.Length..]);
        var payload = data[(type.Length + 2)..];
        int offset = 0;
        if (fragmented)
        {
            if (payload.Length < 4)
            {
                return Fail(service, NotEnoughData, op, tag.Name, count);
            }
            offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
            payload = payload[4..];
            if (offset % tag.ElementSize != 0)
            {
                return Fail(service, InvalidParameter, op, tag.Name, count);
            }
        }
        if (count == 0 || index < 0 || index + count > tag.Count || offset > count * tag.ElementSize)
        {
            return Fail(service, Extended, op, tag.Name, count, BeyondEnd);
        }
        int expected = fragmented ? Math.Min(payload.Length / tag.ElementSize * tag.ElementSize, count * tag.ElementSize - offset) : count * tag.ElementSize;
        if (payload.Length < expected || (fragmented && payload.Length % tag.ElementSize != 0))
        {
            return Fail(service, NotEnoughData, op, tag.Name, count);
        }
        if (payload.Length > expected)
        {
            return Fail(service, TooMuchData, op, tag.Name, count);
        }
        if (tag.Variable.Table.IsReadOnly())
        {
            return Fail(service, PrivilegeViolation, op, tag.Name, count);
        }
        try
        {
            tag.Decode(ctx.Table, index + offset / tag.ElementSize, payload[..expected]);
        }
        catch (ArgumentException)
        {
            return Fail(service, InvalidParameter, op, tag.Name, count);
        }
        return new CipOutcome(Reply(service, Ok, []), op, tag.Name, count, null);
    }

    // --- objects ----------------------------------------------------------------------------------------

    private static CipOutcome Identity(byte service, CipPath path, CipContext ctx)
    {
        const string op = "identity";
        var target = $"{service:X2}";
        if (service == 0x01)
        {
            return new CipOutcome(Reply(service, Ok, IdentityAttributes(ctx)), op, target, 0, null);
        }
        if (service == 0x0E)
        {
            byte[]? value = path.Attribute switch
            {
                1 => U16(VendorId),
                2 => U16(DeviceType),
                3 => U16(ProductCode),
                4 => [RevisionMajor, RevisionMinor],
                5 => U16(0),
                6 => U32(SerialNumber),
                7 => ShortStr(ctx.ProductName),
                _ => null,
            };
            return value is null
                ? Fail(service, AttributeNotSupported, op, $"attr={path.Attribute}")
                : new CipOutcome(Reply(service, Ok, value), op, $"attr={path.Attribute}", 0, null);
        }
        return Fail(service, ServiceNotSupported, $"cmd={service:X2}", op);
    }

    /// <summary>Vendor, device type, product code, revision, status, serial, product name (the Identity attributes in order).</summary>
    public static byte[] IdentityAttributes(CipContext ctx) =>
        [.. U16(VendorId), .. U16(DeviceType), .. U16(ProductCode), RevisionMajor, RevisionMinor, .. U16(0), .. U32(SerialNumber), .. ShortStr(ctx.ProductName)];

    private static CipOutcome MultipleServicePacket(ReadOnlySpan<byte> data, CipContext ctx)
    {
        const string op = "multi";
        if (data.Length < 2)
        {
            return Fail(0x0A, NotEnoughData, op, "?");
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (data.Length < 2 + count * 2)
        {
            return Fail(0x0A, NotEnoughData, op, "?", count);
        }
        var replies = new List<byte[]>(count);
        string? target = null;
        string? error = null;
        for (int i = 0; i < count; i++)
        {
            int start = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + i * 2)..]);
            int end = i + 1 < count ? BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + (i + 1) * 2)..]) : data.Length;
            if (start > end || end > data.Length)
            {
                return Fail(0x0A, InvalidParameter, op, target ?? "?", count);
            }
            var inner = Handle(data[start..end], ctx);
            replies.Add(inner.Reply);
            target ??= inner.Target;
            error ??= inner.Error;
        }
        var body = new List<byte>();
        body.AddRange(U16((ushort)count));
        int offset = 2 + count * 2;
        foreach (var r in replies)
        {
            body.AddRange(U16((ushort)offset));
            offset += r.Length;
        }
        foreach (var r in replies)
        {
            body.AddRange(r);
        }
        return new CipOutcome(Reply(0x0A, Ok, body.ToArray()), op, target ?? "?", count, error);
    }

    private static CipOutcome ConnectionManager(byte service, ReadOnlySpan<byte> data, CipContext ctx)
    {
        switch (service)
        {
            case 0x52:
                // unconnected send: tick, timeout, embedded size, embedded request, pad, route path size, reserved, route path
                if (data.Length < 4)
                {
                    return Fail(service, NotEnoughData, "unconnected-send", "?");
                }
                int size = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
                if (4 + size > data.Length)
                {
                    return Fail(service, NotEnoughData, "unconnected-send", "?");
                }
                return Handle(data.Slice(4, size), ctx);

            case 0x54:
            case 0x5B:
                return ForwardOpen(service, data, ctx);

            case 0x4E:
                return ForwardClose(data, ctx);

            default:
                return Fail(service, ServiceNotSupported, $"cmd={service:X2}", "connection-manager");
        }
    }

    private static CipOutcome ForwardOpen(byte service, ReadOnlySpan<byte> data, CipContext ctx)
    {
        const string op = "forward-open";
        bool large = service == 0x5B;
        int paramSize = large ? 4 : 2;
        int fixedLength = 2 + 4 + 4 + 2 + 2 + 4 + 1 + 3 + 4 + paramSize + 4 + paramSize + 1 + 1;
        if (data.Length < fixedLength)
        {
            return Fail(service, NotEnoughData, op, "?");
        }
        uint toOriginator = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);
        ushort serial = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        ushort vendor = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        uint originatorSerial = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        var toTargetRpi = data.Slice(22, 4);
        uint toTargetParams = large ? BinaryPrimitives.ReadUInt32LittleEndian(data[26..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[26..]);
        var toOriginatorRpi = data.Slice(26 + paramSize, 4);
        int connectionSize = large ? (int)(toTargetParams & 0xFFFF) : (int)(toTargetParams & 0x1FF);
        uint toTarget = ctx.Connections.OpenConnection(toOriginator, serial, vendor, originatorSerial, connectionSize);
        var reply = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, toTarget);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), toOriginator);
        data.Slice(10, 8).CopyTo(reply.AsSpan(8));        // serial, vendor, originator serial
        toTargetRpi.CopyTo(reply.AsSpan(16));               // O→T API
        toOriginatorRpi.CopyTo(reply.AsSpan(20));           // T→O API
        return new CipOutcome(Reply(service, Ok, reply), op, $"size={connectionSize}", 1, null);
    }

    private static CipOutcome ForwardClose(ReadOnlySpan<byte> data, CipContext ctx)
    {
        const string op = "forward-close";
        if (data.Length < 10)
        {
            return Fail(0x4E, NotEnoughData, op, "?");
        }
        ushort serial = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        ushort vendor = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        uint originatorSerial = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);
        var target = $"serial={serial}";
        if (!ctx.Connections.CloseConnection(serial, vendor, originatorSerial))
        {
            return Fail(0x4E, ConnectionFailure, op, target, 0, ConnectionNotFound);
        }
        return new CipOutcome(Reply(0x4E, Ok, [.. data.Slice(2, 8), 0, 0]), op, target, 0, null);
    }

    private static CipOutcome SymbolList(CipPath path, ReadOnlySpan<byte> data, CipContext ctx)
    {
        const string op = "tags";
        int start = Math.Max(path.Instance, 0);
        var target = $"from={start}";
        if (data.Length < 2)
        {
            return Fail(0x55, NotEnoughData, op, target);
        }
        int attributeCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (data.Length < 2 + attributeCount * 2)
        {
            return Fail(0x55, NotEnoughData, op, target);
        }
        var attributes = new int[attributeCount];
        for (int i = 0; i < attributeCount; i++)
        {
            attributes[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + i * 2)..]);
            if (attributes[i] is not (1 or 2 or 3 or 4 or 5 or 6 or 8 or 10))
            {
                return Fail(0x55, AttributeNotSupported, op, target);
            }
        }
        var body = new List<byte>();
        int budget = ctx.MaxReply - 4;
        int returned = 0;
        byte status = Ok;
        var variables = ctx.Table.Variables;
        for (int id = Math.Max(start, 1); id <= variables.Count; id++)
        {
            var tag = new TagInfo(variables[id - 1]);
            var entry = new List<byte>();
            entry.AddRange(U32((uint)id));
            foreach (var attribute in attributes)
            {
                switch (attribute)
                {
                    case 1:
                        entry.AddRange(Str16(tag.Name));
                        break;
                    case 2:
                        entry.AddRange(U16(tag.SymbolType));
                        break;
                    case 3 or 4 or 5 or 6:
                        entry.AddRange(U32(0));
                        break;
                    case 8:
                        entry.AddRange(U32((uint)(tag.Count > 1 ? tag.Count : 0)));
                        entry.AddRange(U32(0));
                        entry.AddRange(U32(0));
                        break;
                    default:
                        entry.Add((byte)(tag.Variable.Table.IsReadOnly() ? 1 : 0)); // external access: 0 read/write, 1 read only
                        break;
                }
            }
            if (body.Count + entry.Count > budget)
            {
                status = PartialTransfer;
                break;
            }
            body.AddRange(entry);
            returned++;
        }
        return new CipOutcome(Reply(0x55, status, body.ToArray()), op, target, returned, status == Ok ? null : $"{status:X2}");
    }

    private static CipOutcome Template(byte service, CipPath path, ReadOnlySpan<byte> data)
    {
        const string op = "template";
        var target = $"{path.Instance:X4}";
        if (path.Instance != StringHandle)
        {
            return Fail(service, PathDestinationUnknown, op, target);
        }
        // member info: LEN DINT at 0, DATA SINT[82] at 4; then the names
        byte[] definition =
        [
            .. U16(0), .. U16(0x00C4), .. U32(0),
            .. U16(StringData), .. U16(0x20C2), .. U32(4),
            .. Encoding.ASCII.GetBytes("STRING\0LEN\0DATA\0"),
        ];
        switch (service)
        {
            case 0x03:
                if (data.Length < 2)
                {
                    return Fail(service, NotEnoughData, op, target);
                }
                int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
                var body = new List<byte>(U16((ushort)count));
                for (int i = 0; i < count && 2 + i * 2 + 2 <= data.Length; i++)
                {
                    int attribute = BinaryPrimitives.ReadUInt16LittleEndian(data[(2 + i * 2)..]);
                    body.AddRange(U16((ushort)attribute));
                    byte[]? value = attribute switch
                    {
                        1 => U16(StringHandle),
                        2 => U16(2),
                        4 => U32((uint)((definition.Length + 21 + 3) / 4)),  // object definition size: clients read size*4-21 bytes
                        5 => U32(StringSize),
                        _ => null,
                    };
                    if (value is null)
                    {
                        body.AddRange(U16(AttributeNotSupported));
                    }
                    else
                    {
                        body.AddRange(U16(0));
                        body.AddRange(value);
                    }
                }
                return new CipOutcome(Reply(service, Ok, body.ToArray()), op, target, 0, null);

            case 0x4C:
                if (data.Length < 6)
                {
                    return Fail(service, NotEnoughData, op, target);
                }
                int offset = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), (uint)definition.Length);
                return new CipOutcome(Reply(service, Ok, definition.AsSpan(offset).ToArray()), op, target, 0, null);

            default:
                return Fail(service, ServiceNotSupported, $"cmd={service:X2}", op);
        }
    }

    // --- replies ----------------------------------------------------------------------------------------

    private static CipOutcome Fail(byte service, byte status, string op, string target, int points = 0, ushort? extended = null)
    {
        var reply = extended is { } ext
            ? new byte[] { (byte)(service | 0x80), 0, status, 1, (byte)ext, (byte)(ext >> 8) }
            : [(byte)(service | 0x80), 0, status, 0];
        return new CipOutcome(reply, op, target, points, extended is { } e ? $"{status:X2}:{e:X4}" : $"{status:X2}");
    }

    private static byte[] Reply(byte service, byte status, ReadOnlySpan<byte> data) =>
        [(byte)(service | 0x80), 0, status, 0, .. data];

    private static byte[] U16(ushort v) => [(byte)v, (byte)(v >> 8)];
    private static byte[] U32(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    private static byte[] ShortStr(string s) => [(byte)s.Length, .. Encoding.ASCII.GetBytes(s)];
    private static byte[] Str16(string s) => [.. U16((ushort)s.Length), .. Encoding.ASCII.GetBytes(s)];
}

/// <summary>A parsed request path: a symbolic tag (possibly with an element index) or a logical class / instance / attribute.</summary>
internal readonly record struct CipPath(string? Symbol, int? Element, int Class, int Instance, int Attribute, bool Valid)
{
    public static CipPath Parse(ReadOnlySpan<byte> path)
    {
        string? symbol = null;
        int? element = null;
        int cls = -1, instance = -1, attribute = -1;
        int at = 0;
        while (at < path.Length)
        {
            byte seg = path[at];
            switch (seg)
            {
                case 0x91:
                    if (at + 2 > path.Length) return Invalid;
                    int len = path[at + 1];
                    if (at + 2 + len > path.Length) return Invalid;
                    var name = Encoding.ASCII.GetString(path.Slice(at + 2, len));
                    symbol = symbol is null ? name : symbol + "." + name;
                    at += 2 + len + (len % 2);
                    break;
                case 0x28:
                    if (at + 2 > path.Length) return Invalid;
                    element = path[at + 1];
                    at += 2;
                    break;
                case 0x29:
                    if (at + 4 > path.Length) return Invalid;
                    element = BinaryPrimitives.ReadUInt16LittleEndian(path[(at + 2)..]);
                    at += 4;
                    break;
                case 0x2A:
                    if (at + 6 > path.Length) return Invalid;
                    element = (int)BinaryPrimitives.ReadUInt32LittleEndian(path[(at + 2)..]);
                    at += 6;
                    break;
                case 0x20 or 0x24 or 0x30:
                    if (at + 2 > path.Length) return Invalid;
                    Set(seg, path[at + 1], ref cls, ref instance, ref attribute);
                    at += 2;
                    break;
                case 0x21 or 0x25 or 0x31:
                    if (at + 4 > path.Length) return Invalid;
                    Set(seg, BinaryPrimitives.ReadUInt16LittleEndian(path[(at + 2)..]), ref cls, ref instance, ref attribute);
                    at += 4;
                    break;
                default:
                    return Invalid;
            }
        }
        return new CipPath(symbol, element, cls, instance, attribute, symbol is not null || cls >= 0);
    }

    private static void Set(byte seg, int value, ref int cls, ref int instance, ref int attribute)
    {
        switch (seg & 0xFC)
        {
            case 0x20: cls = value; break;
            case 0x24: instance = value; break;
            default: attribute = value; break;
        }
    }

    private static CipPath Invalid => new(null, null, -1, -1, -1, false);
}

/// <summary>A variable seen as a CIP tag: its atomic type code (or the STRING structure), element size and encoders.</summary>
internal readonly record struct TagInfo(Variable Variable)
{
    public string Name => Variable.Name;
    public int Count => Variable.Count;
    public bool IsString => Variable.Type == VariableType.String;

    public static bool TryFind(VariableTable table, string name, out TagInfo tag)
    {
        foreach (var v in table.Variables)
        {
            if (v.Name == name)
            {
                tag = new TagInfo(v);
                return true;
            }
        }
        tag = default;
        return false;
    }

    public ushort TypeCode => Variable.Type switch
    {
        VariableType.Bool or VariableType.BoolWord => 0xC1,
        VariableType.Int => 0xC3,
        VariableType.Uint => 0xC7,
        VariableType.Dint or VariableType.Time => 0xC4,
        VariableType.Udint or VariableType.Tod => 0xC8,
        VariableType.Real => 0xCA,
        VariableType.Lint => 0xC5,
        VariableType.Ulint or VariableType.Date or VariableType.Dt => 0xC9,
        _ => CipRouter.StringHandle,
    };

    public int ElementSize => Variable.Type switch
    {
        VariableType.Bool or VariableType.BoolWord => 1,
        VariableType.Int or VariableType.Uint => 2,
        VariableType.Dint or VariableType.Time or VariableType.Udint or VariableType.Tod or VariableType.Real => 4,
        VariableType.Lint or VariableType.Ulint or VariableType.Date or VariableType.Dt => 8,
        _ => CipRouter.StringSize,
    };

    /// <summary>The type prefix of Read / Write Tag data: the atomic code, or A0 02 + the structure handle.</summary>
    public byte[] TypeBytes => IsString
        ? [0xA0, 0x02, unchecked((byte)CipRouter.StringHandle), (byte)(CipRouter.StringHandle >> 8)]
        : [(byte)TypeCode, (byte)(TypeCode >> 8)];

    /// <summary>Symbol object attribute 2: structure bit + handle, or the atomic code with the array bit.</summary>
    public ushort SymbolType => IsString ? (ushort)(0x8000 | CipRouter.StringHandle) : (ushort)(TypeCode | (Count > 1 ? 0x2000 : 0));

    private delegate void Writer<T>(Span<byte> dest, T value);
    private delegate T Reader<T>(ReadOnlySpan<byte> src);

    /// <summary>Elements index..index+count as wire bytes (little-endian).</summary>
    public byte[] Encode(VariableTable t, int index, int count) => Variable.Type switch
    {
        VariableType.Bool or VariableType.BoolWord => Enc(t.ReadAll<bool>(Name), index, count, (d, v) => d[0] = (byte)(v ? 1 : 0)),
        VariableType.Int => Enc(t.ReadAll<int>(Name), index, count, (d, v) => BinaryPrimitives.WriteInt16LittleEndian(d, (short)v)),
        VariableType.Uint => Enc(t.ReadAll<uint>(Name), index, count, (d, v) => BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)v)),
        VariableType.Dint or VariableType.Time => Enc(t.ReadAll<int>(Name), index, count, BinaryPrimitives.WriteInt32LittleEndian),
        VariableType.Udint or VariableType.Tod => Enc(t.ReadAll<uint>(Name), index, count, BinaryPrimitives.WriteUInt32LittleEndian),
        VariableType.Real => Enc(t.ReadAll<float>(Name), index, count, BinaryPrimitives.WriteSingleLittleEndian),
        VariableType.Lint => Enc(t.ReadAll<long>(Name), index, count, BinaryPrimitives.WriteInt64LittleEndian),
        VariableType.Ulint or VariableType.Date or VariableType.Dt => Enc(t.ReadAll<ulong>(Name), index, count, BinaryPrimitives.WriteUInt64LittleEndian),
        _ => Enc(t.ReadAll<string>(Name), index, count, WriteString),
    };

    /// <summary>Wire bytes → elements from index (the rest of the array is kept; one device write).</summary>
    public void Decode(VariableTable t, int index, ReadOnlySpan<byte> data)
    {
        switch (Variable.Type)
        {
            case VariableType.Bool or VariableType.BoolWord:
                Put(t, index, Dec(data, 1, s => s[0] != 0));
                break;
            case VariableType.Int:
                Put(t, index, Dec(data, 2, s => (int)BinaryPrimitives.ReadInt16LittleEndian(s)));
                break;
            case VariableType.Uint:
                Put(t, index, Dec(data, 2, s => (uint)BinaryPrimitives.ReadUInt16LittleEndian(s)));
                break;
            case VariableType.Dint or VariableType.Time:
                Put(t, index, Dec(data, 4, BinaryPrimitives.ReadInt32LittleEndian));
                break;
            case VariableType.Udint or VariableType.Tod:
                Put(t, index, Dec(data, 4, BinaryPrimitives.ReadUInt32LittleEndian));
                break;
            case VariableType.Real:
                Put(t, index, Dec(data, 4, BinaryPrimitives.ReadSingleLittleEndian));
                break;
            case VariableType.Lint:
                Put(t, index, Dec(data, 8, BinaryPrimitives.ReadInt64LittleEndian));
                break;
            case VariableType.Ulint or VariableType.Date or VariableType.Dt:
                Put(t, index, Dec(data, 8, BinaryPrimitives.ReadUInt64LittleEndian));
                break;
            default:
                Put(t, index, Dec(data, CipRouter.StringSize, ReadString));
                break;
        }
    }

    private byte[] Enc<T>(T[] all, int index, int count, Writer<T> write)
    {
        var bytes = new byte[count * ElementSize];
        for (int i = 0; i < count; i++)
        {
            write(bytes.AsSpan(i * ElementSize, ElementSize), all[index + i]);
        }
        return bytes;
    }

    private static T[] Dec<T>(ReadOnlySpan<byte> data, int size, Reader<T> read)
    {
        var values = new T[data.Length / size];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = read(data.Slice(i * size, size));
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

    /// <summary>Logix STRING: LEN (DINT) + DATA (SINT[82]) + 2 pad bytes.</summary>
    private static void WriteString(Span<byte> dest, string value)
    {
        dest.Clear();
        var bytes = Encoding.UTF8.GetBytes(value);
        int len = Math.Min(bytes.Length, CipRouter.StringData);
        BinaryPrimitives.WriteInt32LittleEndian(dest, len);
        bytes.AsSpan(0, len).CopyTo(dest[4..]);
    }

    private static string ReadString(ReadOnlySpan<byte> src)
    {
        int len = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(src), 0, CipRouter.StringData);
        return Encoding.UTF8.GetString(src.Slice(4, len));
    }
}
