using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Moz.Devices;

namespace Moz.S7;

/// <summary>
/// One S7 memory area: inputs (0x81, I/E), outputs (0x82, Q/A), flags
/// (0x83, M) or one data block (0x84, DB n). S7 memory is byte addressed
/// and big-endian; moz keeps it in word tables (byte b ↔ the high (even b)
/// or low (odd b) byte of word b/2), so MW10 is words[5].
/// </summary>
public readonly record struct S7Area(byte Code, int Db)
{
    public const byte Inputs = 0x81;
    public const byte Outputs = 0x82;
    public const byte Flags = 0x83;
    public const byte DataBlock = 0x84;

    public string Name => Code switch
    {
        Inputs => "I",
        Outputs => "Q",
        Flags => "M",
        _ => $"DB{Db}",
    };

    /// <summary>"DB10", "M", "I" / "E", "Q" / "A" (case-insensitive).</summary>
    public static bool TryFromCode(string text, out S7Area area)
    {
        var upper = text.Trim().ToUpperInvariant();
        switch (upper)
        {
            case "I" or "E":
                area = new S7Area(Inputs, 0);
                return true;
            case "Q" or "A":
                area = new S7Area(Outputs, 0);
                return true;
            case "M":
                area = new S7Area(Flags, 0);
                return true;
        }
        if (upper.Length > 2 && upper.StartsWith("DB", StringComparison.Ordinal)
            && int.TryParse(upper.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int db) && db >= 1 && db <= 65535)
        {
            area = new S7Area(DataBlock, db);
            return true;
        }
        area = default;
        return false;
    }

    /// <summary>The area of a request item; false for timers, counters and anything else moz does not serve.</summary>
    public static bool TryFromWire(byte code, int db, out S7Area area)
    {
        switch (code)
        {
            case Inputs or Outputs or Flags:
                area = new S7Area(code, 0);
                return true;
            case DataBlock when db >= 1:
                area = new S7Area(code, db);
                return true;
            default:
                area = default;
                return false;
        }
    }

    /// <summary>Log notation: width 0 = bit ("DB1.DBX10.3", "M0.1"), 1 = byte ("DB1.DBB11", "MB5"), 2 = word ("QW2"), 4 = double word ("MD8").</summary>
    public string Format(int byteAddress, int bit, int width)
    {
        var prefix = Code == DataBlock ? $"DB{Db}.DB" : Name;
        return width switch
        {
            0 => $"{prefix}{(Code == DataBlock ? "X" : "")}{byteAddress}.{bit}",
            1 => $"{prefix}B{byteAddress}",
            2 => $"{prefix}W{byteAddress}",
            _ => $"{prefix}D{byteAddress}",
        };
    }
}

/// <summary>Where one S7 area lives: byte b of the area ↔ byte b of word table[Offset + b/2], for words Offset..Offset+Count-1.</summary>
public readonly record struct S7Map(S7Area Area, TableKind Table, int Offset, int Count)
{
    /// <summary>The default map: DB1 → words over the whole table. M / I / Q and other DBs need [[server.map]].</summary>
    public static S7Map[] Defaults(IDeviceAccess device) =>
        [new S7Map(new S7Area(S7Area.DataBlock, 1), TableKind.Words, 0, device.Length(TableKind.Words))];

    /// <summary>Defaults plus user maps; a user map for DB1 replaces the default one.</summary>
    public static S7Map[] Merge(IDeviceAccess device, IEnumerable<S7Map> overrides)
    {
        var list = new List<S7Map>(overrides);
        foreach (var d in Defaults(device))
        {
            if (!list.Any(m => m.Area == d.Area))
            {
                list.Add(d);
            }
        }
        return [.. list];
    }
}

/// <summary>Per-connection state: the PDU length negotiated by Setup Communication (240 until then).</summary>
public sealed class S7Session
{
    public int PduLength { get; set; } = 240;
}

/// <summary>What the server did with one PDU: the response PDU plus the log fields (Error = first item return code or the PDU error, hex).</summary>
public readonly record struct S7Outcome(byte[] Response, string Op, string Target, int Points, string? Error);

/// <summary>
/// Server half of S7comm (the S7-300/400 "classic" protocol): one PDU in,
/// memory operation through <see cref="IDeviceAccess"/>, response PDU out.
/// Pure apart from the device access; no sockets, no ISO-on-TCP framing.
/// Jobs: Setup Communication (PDU up to 960), Read Var / Write Var with
/// several S7ANY items (BIT / BYTE / CHAR / WORD / INT / DWORD / DINT /
/// REAL transports, odd byte addresses, bit addresses); user data: SZL
/// read of 0x0011 and 0x001C answering as a CPU 315-2 PN/DP. Everything
/// else is answered with error 0x8104.
/// </summary>
public static class S7Server
{
    public const int PduMax = 960;
    private const int JobHeader = 10;
    private const int AckHeader = 12;

    public const byte ReturnOk = 0xFF;
    public const byte ReturnNotAllowed = 0x03;
    public const byte ReturnOutOfRange = 0x05;
    public const byte ReturnTypeNotSupported = 0x06;
    public const byte ReturnSizeMismatch = 0x07;
    public const byte ReturnNoObject = 0x0A;

    private const ushort ErrorNotSupported = 0x8104;
    private const ushort ErrorDataAccess = 0x8500;

    public const string OrderNumber = "6ES7 315-2EH14-0AB0 ";
    public const string ModuleName = "CPU 315-2 PN/DP";

    /// <summary>False when the bytes are not a job or user-data PDU (bad protocol id, cut short, lengths inconsistent): the transport logs it as malformed.</summary>
    public static bool TryHandle(ReadOnlySpan<byte> pdu, IDeviceAccess device, IReadOnlyList<S7Map> maps, S7Session session, out S7Outcome outcome)
    {
        outcome = default;
        if (pdu.Length < JobHeader || pdu[0] != 0x32 || pdu[1] is not (1 or 7))
        {
            return false;
        }
        int paramLength = BinaryPrimitives.ReadUInt16BigEndian(pdu[6..]);
        int dataLength = BinaryPrimitives.ReadUInt16BigEndian(pdu[8..]);
        if (JobHeader + paramLength + dataLength > pdu.Length)
        {
            return false;
        }
        var reference = pdu.Slice(4, 2);
        var param = pdu.Slice(JobHeader, paramLength);
        var data = pdu.Slice(JobHeader + paramLength, dataLength);
        if (pdu[1] == 7)
        {
            outcome = UserData(reference, param, data);
            return true;
        }
        if (param.Length == 0)
        {
            return false;
        }
        outcome = param[0] switch
        {
            0xF0 => Setup(reference, param, session),
            0x04 => ReadVar(reference, param, device, maps, session),
            0x05 => WriteVar(reference, param, data, device, maps),
            _ => new S7Outcome(Ack(reference, [param[0]], [], ErrorNotSupported), $"cmd={param[0]:X2}", "?", 0, "8104"),
        };
        return true;
    }

    // --- setup communication ---------------------------------------------------------------------

    private static S7Outcome Setup(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> param, S7Session session)
    {
        if (param.Length < 8)
        {
            return new S7Outcome(Ack(reference, [0xF0], [], ErrorNotSupported), "setup", "?", 0, "8104");
        }
        int requested = BinaryPrimitives.ReadUInt16BigEndian(param[6..]);
        int granted = Math.Clamp(requested, 1, PduMax);
        session.PduLength = granted;
        var response = param[..8].ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), (ushort)granted);
        return new S7Outcome(Ack(reference, response, [], 0), "setup", $"pdu={granted}", 0, null);
    }

    // --- read / write var ------------------------------------------------------------------------

    /// <summary>One S7ANY item: transport size, element count, area, byte address and bit.</summary>
    private readonly record struct Item(byte Transport, int Count, byte AreaCode, int Db, int ByteAddress, int Bit)
    {
        /// <summary>Bytes per element: 0 for BIT, -1 for transports moz does not serve.</summary>
        public int ElementBytes => Transport switch
        {
            1 => 0,
            2 or 3 => 1,
            4 or 5 => 2,
            6 or 7 or 8 => 4,
            _ => -1,
        };

        public int ByteCount => ElementBytes == 0 ? (Bit + Count + 7) / 8 : Count * ElementBytes;

        public string Target => S7Area.TryFromWire(AreaCode, Db, out var area)
            ? area.Format(ByteAddress, Bit, ElementBytes < 0 ? 1 : ElementBytes)
            : $"area={AreaCode:X2}";
    }

    private static bool TryParseItems(ReadOnlySpan<byte> param, out Item[] items)
    {
        items = [];
        if (param.Length < 2)
        {
            return false;
        }
        int count = param[1];
        if (param.Length != 2 + count * 12)
        {
            return false;
        }
        items = new Item[count];
        for (int i = 0; i < count; i++)
        {
            var it = param.Slice(2 + i * 12, 12);
            if (it[0] != 0x12 || it[1] != 0x0A || it[2] != 0x10)
            {
                return false;
            }
            int address = (it[9] << 16) | (it[10] << 8) | it[11];
            items[i] = new Item(it[3], BinaryPrimitives.ReadUInt16BigEndian(it[4..]), it[8],
                BinaryPrimitives.ReadUInt16BigEndian(it[6..]), address >> 3, address & 7);
        }
        return true;
    }

    /// <summary>Locate the item in a map: the return code is OK, or why not (type, object, range).</summary>
    private static byte Locate(Item item, IReadOnlyList<S7Map> maps, out S7Map map)
    {
        map = default;
        if (item.ElementBytes < 0)
        {
            return ReturnTypeNotSupported;
        }
        if (!S7Area.TryFromWire(item.AreaCode, item.Db, out var area))
        {
            return ReturnNoObject;
        }
        int found = -1;
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].Area == area)
            {
                found = i;
                break;
            }
        }
        if (found < 0)
        {
            return ReturnNoObject;
        }
        map = maps[found];
        if (item.Count == 0 || item.Bit > 7 || (long)item.ByteAddress + item.ByteCount > (long)map.Count * 2)
        {
            return ReturnOutOfRange;
        }
        return ReturnOk;
    }

    private static S7Outcome ReadVar(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> param, IDeviceAccess device,
        IReadOnlyList<S7Map> maps, S7Session session)
    {
        if (!TryParseItems(param, out var items))
        {
            return new S7Outcome(Ack(reference, [0x04], [], ErrorDataAccess), "read", "?", 0, "8500");
        }
        var data = new List<byte>();
        string? error = null;
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            byte rc = Locate(item, maps, out var map);
            if (rc != ReturnOk)
            {
                error ??= $"{rc:X2}";
                data.AddRange([rc, 0, 0, 0]);
                continue;
            }
            if (item.ElementBytes == 0)
            {
                var bytes = ReadBytes(device, map, item.ByteAddress, item.ByteCount);
                data.AddRange([ReturnOk, 0x03, (byte)(item.Count >> 8), (byte)item.Count]);
                for (int b = 0; b < item.Count; b++)
                {
                    int bitIndex = item.Bit + b;
                    data.Add((byte)((bytes[bitIndex / 8] >> (bitIndex % 8)) & 1));
                }
                if (item.Count % 2 == 1 && i < items.Length - 1)
                {
                    data.Add(0);
                }
            }
            else
            {
                var bytes = ReadBytes(device, map, item.ByteAddress, item.ByteCount);
                int bits = item.ByteCount * 8;
                data.AddRange([ReturnOk, 0x04, (byte)(bits >> 8), (byte)bits]);
                data.AddRange(bytes);
                if (bytes.Length % 2 == 1 && i < items.Length - 1)
                {
                    data.Add(0);
                }
            }
        }
        var target = items.Length > 0 ? items[0].Target : "?";
        if (AckHeader + 2 + data.Count > session.PduLength)
        {
            return new S7Outcome(Ack(reference, [0x04], [], ErrorDataAccess), "read", target, items.Length, "8500");
        }
        return new S7Outcome(Ack(reference, [0x04, (byte)items.Length], data.ToArray(), 0), "read", target, items.Length, error);
    }

    private static S7Outcome WriteVar(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> param, ReadOnlySpan<byte> data,
        IDeviceAccess device, IReadOnlyList<S7Map> maps)
    {
        if (!TryParseItems(param, out var items))
        {
            return new S7Outcome(Ack(reference, [0x05], [], ErrorDataAccess), "write", "?", 0, "8500");
        }
        // the data items carry their own lengths: slice them all first so a bad item does not shift the rest
        var payloads = new byte[items.Length][];
        int at = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (at + 4 > data.Length)
            {
                return new S7Outcome(Ack(reference, [0x05], [], ErrorDataAccess), "write", items[0].Target, items.Length, "8500");
            }
            byte transport = data[at + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            int bytes = transport switch
            {
                0x03 => length,         // one byte per bit
                0x09 => length,         // octet string: bytes
                _ => (length + 7) / 8,  // bits
            };
            if (at + 4 + bytes > data.Length)
            {
                return new S7Outcome(Ack(reference, [0x05], [], ErrorDataAccess), "write", items[0].Target, items.Length, "8500");
            }
            payloads[i] = data.Slice(at + 4, bytes).ToArray();
            at += 4 + bytes + (bytes % 2 == 1 && i < items.Length - 1 ? 1 : 0);
        }

        var results = new byte[items.Length];
        string? error = null;
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            byte rc = Locate(item, maps, out var map);
            if (rc == ReturnOk)
            {
                int expected = item.ElementBytes == 0 ? item.Count : item.ByteCount;
                if (payloads[i].Length != expected)
                {
                    rc = ReturnSizeMismatch;
                }
                else if (map.Table.IsReadOnly())
                {
                    rc = ReturnNotAllowed;
                }
                else if (item.ElementBytes == 0)
                {
                    var bytes = ReadBytes(device, map, item.ByteAddress, item.ByteCount);
                    for (int b = 0; b < item.Count; b++)
                    {
                        int bitIndex = item.Bit + b;
                        int mask = 1 << (bitIndex % 8);
                        bytes[bitIndex / 8] = (byte)(payloads[i][b] != 0 ? bytes[bitIndex / 8] | mask : bytes[bitIndex / 8] & ~mask);
                    }
                    WriteBytes(device, map, item.ByteAddress, bytes);
                }
                else
                {
                    WriteBytes(device, map, item.ByteAddress, payloads[i]);
                }
            }
            results[i] = rc;
            if (rc != ReturnOk)
            {
                error ??= $"{rc:X2}";
            }
        }
        var target = items.Length > 0 ? items[0].Target : "?";
        return new S7Outcome(Ack(reference, [0x05, (byte)items.Length], results, 0), "write", target, items.Length, error);
    }

    /// <summary>Bytes byteAddress..+count of the area out of its big-endian words (any alignment).</summary>
    private static byte[] ReadBytes(IDeviceAccess device, S7Map map, int byteAddress, int count)
    {
        int firstWord = byteAddress / 2;
        int lastWord = (byteAddress + count - 1) / 2;
        var words = new ushort[lastWord - firstWord + 1];
        device.Read(map.Table, map.Offset + firstWord, words);
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            int b = byteAddress + i;
            ushort w = words[b / 2 - firstWord];
            bytes[i] = (byte)(b % 2 == 0 ? w >> 8 : w);
        }
        return bytes;
    }

    /// <summary>Overlay bytes onto the area: the words at the edges are read-modify-written, the range is written in one call.</summary>
    private static void WriteBytes(IDeviceAccess device, S7Map map, int byteAddress, ReadOnlySpan<byte> bytes)
    {
        int firstWord = byteAddress / 2;
        int lastWord = (byteAddress + bytes.Length - 1) / 2;
        var words = new ushort[lastWord - firstWord + 1];
        device.Read(map.Table, map.Offset + firstWord, words);
        for (int i = 0; i < bytes.Length; i++)
        {
            int b = byteAddress + i;
            int w = b / 2 - firstWord;
            words[w] = (ushort)(b % 2 == 0 ? (words[w] & 0x00FF) | (bytes[i] << 8) : (words[w] & 0xFF00) | bytes[i]);
        }
        device.Write(map.Table, map.Offset + firstWord, words);
    }

    // --- user data: SZL ---------------------------------------------------------------------------

    private static S7Outcome UserData(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> param, ReadOnlySpan<byte> data)
    {
        // parameter: 00 01 12 | 04 | 11 (request) | 44 (CPU functions) | 01 (read SZL) | sequence; data: FF 09 len | SZL id | index
        bool szlRead = param.Length >= 8 && param[0] == 0 && param[1] == 1 && param[2] == 0x12 && param[3] == 4
            && param[5] == 0x44 && param[6] == 1 && data.Length >= 8 && data[0] == ReturnOk;
        byte sequence = param.Length >= 8 ? param[7] : (byte)0;
        if (!szlRead)
        {
            return new S7Outcome(UserDataResponse(reference, sequence, ErrorNotSupported, [ReturnNoObject, 0, 0, 0]), "cmd=ud", "?", 0, "8104");
        }
        int szlId = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int index = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        var target = $"{szlId:X4}";
        (int, List<byte[]>)? records = szlId switch
        {
            0x0011 => ModuleIdentification(),
            0x001C => ComponentIdentification(),
            _ => null,
        };
        if (records is null)
        {
            return new S7Outcome(UserDataResponse(reference, sequence, ErrorNotSupported, [ReturnNoObject, 0, 0, 0]), "szl", target, 0, "8104");
        }
        var (recordLength, list) = records.Value;
        var body = new List<byte>();
        body.AddRange([(byte)(szlId >> 8), (byte)szlId, (byte)(index >> 8), (byte)index]);
        body.AddRange([(byte)(recordLength >> 8), (byte)recordLength, (byte)(list.Count >> 8), (byte)list.Count]);
        foreach (var record in list)
        {
            body.AddRange(record);
        }
        var szlData = new byte[4 + body.Count];
        szlData[0] = ReturnOk;
        szlData[1] = 0x09;
        BinaryPrimitives.WriteUInt16BigEndian(szlData.AsSpan(2), (ushort)body.Count);
        body.CopyTo(szlData, 4);
        return new S7Outcome(UserDataResponse(reference, sequence, 0, szlData), "szl", target, list.Count, null);
    }

    /// <summary>SZL 0x0011 (module identification): 28-byte records, index + order number + type + versions.</summary>
    private static (int, List<byte[]>) ModuleIdentification()
    {
        var list = new List<byte[]>();
        foreach (int index in new[] { 1, 6, 7 })
        {
            var record = new byte[28];
            record[1] = (byte)index;
            Encoding.ASCII.GetBytes(OrderNumber, record.AsSpan(2, 20));
            record[22] = 0xC0; // BGTyp
            record[25] = 1;    // Ausbg1
            record[27] = 1;    // Ausbe
            list.Add(record);
        }
        return (28, list);
    }

    /// <summary>SZL 0x001C (component identification): 34-byte records, index + 32 ASCII bytes.</summary>
    private static (int, List<byte[]>) ComponentIdentification()
    {
        var list = new List<byte[]>();
        foreach (var (index, text) in new[]
        {
            (1, "moz"), (2, ModuleName), (3, "moz"), (4, "Original Siemens Equipment"),
            (5, "S C-MOZ0000001"), (7, ModuleName), (8, "MMC 0000000000"),
        })
        {
            var record = new byte[34];
            record[1] = (byte)index;
            Encoding.ASCII.GetBytes(text, record.AsSpan(2, 32));
            list.Add(record);
        }
        return (34, list);
    }

    // --- PDU building -----------------------------------------------------------------------------

    /// <summary>ack_data: 32 03 | 0000 | reference | param length | data length | error class, code | param | data.</summary>
    private static byte[] Ack(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> param, ReadOnlySpan<byte> data, ushort error)
    {
        var pdu = new byte[AckHeader + param.Length + data.Length];
        pdu[0] = 0x32;
        pdu[1] = 0x03;
        reference.CopyTo(pdu.AsSpan(4, 2));
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6), (ushort)param.Length);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(8), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(10), error);
        param.CopyTo(pdu.AsSpan(AckHeader));
        data.CopyTo(pdu.AsSpan(AckHeader + param.Length));
        return pdu;
    }

    /// <summary>user data response: 32 07 | 0000 | reference | 000C | data length | 00 01 12 08 12 84 01 seq 00 00 error | data.</summary>
    private static byte[] UserDataResponse(ReadOnlySpan<byte> reference, byte sequence, ushort error, ReadOnlySpan<byte> data)
    {
        var pdu = new byte[JobHeader + 12 + data.Length];
        pdu[0] = 0x32;
        pdu[1] = 0x07;
        reference.CopyTo(pdu.AsSpan(4, 2));
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6), 12);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(8), (ushort)data.Length);
        byte[] param = [0x00, 0x01, 0x12, 0x08, 0x12, 0x84, 0x01, sequence, 0x00, 0x00, (byte)(error >> 8), (byte)error];
        param.CopyTo(pdu.AsSpan(JobHeader));
        data.CopyTo(pdu.AsSpan(JobHeader + 12));
        return pdu;
    }
}
