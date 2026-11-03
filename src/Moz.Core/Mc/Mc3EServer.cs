using Moz.Devices;

namespace Moz.Mc;

/// <summary>
/// Where one MC device type lives in the simulated memory: device number n ↔
/// table[Offset + n] for n in 0..Count-1. A bit device maps to a bit table,
/// a word device to a word table. Devices without a map are not served
/// (end code C05B).
/// </summary>
public readonly record struct McMap(McDevice Device, TableKind Table, int Offset, int Count)
{
    /// <summary>
    /// The default map: D → holding registers, M → coils, X → discrete inputs,
    /// each from 0 over the whole table. Everything else needs [[server.map]].
    /// </summary>
    public static McMap[] Defaults(IDeviceAccess device) =>
    [
        Whole(device, "D", TableKind.HoldingRegister),
        Whole(device, "M", TableKind.Coil),
        Whole(device, "X", TableKind.DiscreteInput),
    ];

    /// <summary>Defaults plus user maps; a user map for D/M/X replaces the default one.</summary>
    public static McMap[] Merge(IDeviceAccess device, IEnumerable<McMap> overrides)
    {
        var list = new List<McMap>(overrides);
        foreach (var d in Defaults(device))
        {
            if (!list.Any(m => m.Device.Code == d.Device.Code))
            {
                list.Add(d);
            }
        }
        return [.. list];
    }

    private static McMap Whole(IDeviceAccess device, string code, TableKind table)
    {
        McDevice.TryFromCode(code, out var dev);
        return new McMap(dev, table, 0, device.Length(table));
    }
}

/// <summary>What the server did with one request: the response frame plus the log fields.</summary>
public readonly record struct McOutcome(byte[] Response, string Op, string Target, int Points, ushort EndCode);

/// <summary>
/// Server half of the MC 3E binary codec: request frame in, memory
/// operation through <see cref="IDeviceAccess"/>, response frame out. Pure
/// apart from the device access; no sockets. Supported: batch read 0401 and
/// batch write 1401, word units on word devices and bit units on bit
/// devices. End codes: C059 unknown command/subcommand, C05B unknown or
/// unmapped device, C05C units not matching the device (or a write to a
/// read-only table), C051 points out of range, C056 beyond the mapped
/// range, C061 request data length inconsistent.
/// </summary>
public static class Mc3EServer
{
    public const ushort EndOk = 0x0000;
    public const ushort EndPoints = 0xC051;
    public const ushort EndRange = 0xC056;
    public const ushort EndCommand = 0xC059;
    public const ushort EndDevice = 0xC05B;
    public const ushort EndContents = 0xC05C;
    public const ushort EndLength = 0xC061;

    private const int MinBody = 12; // timer(2) + command(2) + subcommand(2) + head(3) + code(1) + points(2)

    /// <summary>Parse a request header: subheader 5000 and a data length that can hold at least a command.</summary>
    public static bool TryParseRequestHeader(ReadOnlySpan<byte> header, out int dataLength)
    {
        dataLength = 0;
        if (header.Length < Mc3E.HeaderSize || header[0] != 0x50 || header[1] != 0x00)
        {
            return false;
        }
        dataLength = header[7] | (header[8] << 8);
        return dataLength >= 4;
    }

    /// <summary>Handle one request (header + body) against the device through the maps.</summary>
    public static McOutcome Handle(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body, IDeviceAccess device, IReadOnlyList<McMap> maps)
    {
        var routing = header.Slice(2, 5);
        if (body.Length < MinBody)
        {
            return Error(routing, body, EndLength, "?", "?", 0);
        }
        int command = body[2] | (body[3] << 8);
        int subcommand = body[4] | (body[5] << 8);
        int head = body[6] | (body[7] << 8) | (body[8] << 16);
        byte code = body[9];
        int points = body[10] | (body[11] << 8);
        var op = command switch { 0x0401 => "read", 0x1401 => "write", _ => $"cmd={command:X4}" };
        var target = McDevice.TryFromBinary(code, out var dev) ? dev.Format(head) : $"code={code:X2}";

        if (command is not (0x0401 or 0x1401) || subcommand is not (0 or 1))
        {
            return Error(routing, body, EndCommand, op, target, points);
        }
        if (dev.Code is null)
        {
            return Error(routing, body, EndDevice, op, target, points);
        }
        int found = -1;
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].Device.Code == dev.Code)
            {
                found = i;
                break;
            }
        }
        if (found < 0)
        {
            return Error(routing, body, EndDevice, op, target, points);
        }
        var map = maps[found];
        if ((subcommand == 1) != dev.IsBit)
        {
            return Error(routing, body, EndContents, op, target, points);
        }
        int max = dev.IsBit ? Mc3E.ReadBitsMax : Mc3E.ReadWordsMax;
        if (points < 1 || points > max)
        {
            return Error(routing, body, EndPoints, op, target, points);
        }
        if (head + points > map.Count)
        {
            return Error(routing, body, EndRange, op, target, points);
        }
        int index = map.Offset + head;

        if (command == 0x0401)
        {
            if (dev.IsBit)
            {
                var bits = new bool[points];
                device.Read(map.Table, index, bits);
                var data = new byte[(points + 1) / 2];
                for (int i = 0; i < points; i++)
                {
                    if (bits[i])
                    {
                        data[i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
                    }
                }
                return new McOutcome(Response(routing, EndOk, data), op, target, points, EndOk);
            }
            else
            {
                var words = new ushort[points];
                device.Read(map.Table, index, words);
                var data = new byte[points * 2];
                for (int i = 0; i < points; i++)
                {
                    data[i * 2] = (byte)words[i];
                    data[i * 2 + 1] = (byte)(words[i] >> 8);
                }
                return new McOutcome(Response(routing, EndOk, data), op, target, points, EndOk);
            }
        }

        // batch write
        int dataLength = dev.IsBit ? (points + 1) / 2 : points * 2;
        if (body.Length != MinBody + dataLength)
        {
            return Error(routing, body, EndLength, op, target, points);
        }
        if (map.Table is TableKind.DiscreteInput or TableKind.InputRegister)
        {
            return Error(routing, body, EndContents, op, target, points); // read-only for peers, like Modbus
        }
        var payload = body.Slice(MinBody, dataLength);
        if (dev.IsBit)
        {
            var bits = new bool[points];
            for (int i = 0; i < points; i++)
            {
                bits[i] = (payload[i / 2] & (i % 2 == 0 ? 0x10 : 0x01)) != 0;
            }
            device.Write(map.Table, index, bits);
        }
        else
        {
            var words = new ushort[points];
            for (int i = 0; i < points; i++)
            {
                words[i] = (ushort)(payload[i * 2] | (payload[i * 2 + 1] << 8));
            }
            device.Write(map.Table, index, words);
        }
        return new McOutcome(Response(routing, EndOk, []), op, target, points, EndOk);
    }

    private static McOutcome Error(ReadOnlySpan<byte> routing, ReadOnlySpan<byte> body, ushort endCode, string op, string target, int points)
    {
        // error information: routing echo + command + subcommand (zeros when the body was too short to hold them)
        Span<byte> info = stackalloc byte[9];
        routing.CopyTo(info);
        if (body.Length >= 6)
        {
            body.Slice(2, 4).CopyTo(info[5..]);
        }
        return new McOutcome(Response(routing, endCode, info), op, target, points, endCode);
    }

    /// <summary>D000 | routing echo | length | end code | data.</summary>
    private static byte[] Response(ReadOnlySpan<byte> routing, ushort endCode, ReadOnlySpan<byte> data)
    {
        int length = 2 + data.Length;
        var resp = new byte[Mc3E.HeaderSize + length];
        resp[0] = 0xD0;
        resp[1] = 0x00;
        routing.CopyTo(resp.AsSpan(2, 5));
        resp[7] = (byte)length;
        resp[8] = (byte)(length >> 8);
        resp[9] = (byte)endCode;
        resp[10] = (byte)(endCode >> 8);
        data.CopyTo(resp.AsSpan(11));
        return resp;
    }
}
