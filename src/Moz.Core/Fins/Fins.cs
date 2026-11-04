using System.Globalization;
using Moz.Devices;

namespace Moz.Fins;

/// <summary>
/// One FINS memory area (CS/CJ codes): the code used for word access and
/// the one used for bit access. Areas are word organised; moz serves them
/// from word tables and bit access addresses bits inside those words.
/// EM banks are "E0".."E18" (hexadecimal bank number): banks 0..C use
/// A0..AC / 20..2C, banks D..18 use E0..EB / 60..6B.
/// </summary>
public readonly record struct FinsArea(string Code, byte WordCode, byte BitCode)
{
    public const int BankMax = 0x18;

    private static readonly FinsArea[] Fixed =
    [
        new("CIO", 0xB0, 0x30),
        new("W", 0xB1, 0x31),
        new("H", 0xB2, 0x32),
        new("A", 0xB3, 0x33),
        new("DM", 0x82, 0x02),
    ];

    /// <summary>"DM", "cio", "E0".."E18" (case-insensitive); false for anything else (T/C, IR/DR, ... are not served).</summary>
    public static bool TryFromCode(string text, out FinsArea area)
    {
        area = default;
        var upper = text.Trim().ToUpperInvariant();
        foreach (var a in Fixed)
        {
            if (a.Code == upper)
            {
                area = a;
                return true;
            }
        }
        if (upper.Length is 2 or 3 && upper[0] == 'E'
            && int.TryParse(upper.AsSpan(1), NumberStyles.AllowHexSpecifier, null, out int bank) && bank <= BankMax)
        {
            area = Bank(bank);
            return true;
        }
        return false;
    }

    /// <summary>The area of a wire code and whether that code addresses bits.</summary>
    public static bool TryFromBinary(byte code, out FinsArea area, out bool isBit)
    {
        foreach (var a in Fixed)
        {
            if (a.WordCode == code || a.BitCode == code)
            {
                area = a;
                isBit = a.BitCode == code;
                return true;
            }
        }
        int bank = code switch
        {
            >= 0xA0 and <= 0xAC => code - 0xA0,
            >= 0x20 and <= 0x2C => code - 0x20,
            >= 0xE0 and <= 0xEB => code - 0xE0 + 0xD,
            >= 0x60 and <= 0x6B => code - 0x60 + 0xD,
            _ => -1,
        };
        if (bank < 0)
        {
            area = default;
            isBit = false;
            return false;
        }
        area = Bank(bank);
        isBit = code < 0x80;
        return true;
    }

    private static FinsArea Bank(int bank) => bank <= 0xC
        ? new($"E{bank:X}", (byte)(0xA0 + bank), (byte)(0x20 + bank))
        : new($"E{bank:X}", (byte)(0xE0 + bank - 0xD), (byte)(0x60 + bank - 0xD));

    /// <summary>"DM100", "CIO100.05", "E0_7" (log notation).</summary>
    public string Format(int address, int bit, bool isBit)
    {
        var sep = Code[0] == 'E' && Code.Length > 1 ? "_" : "";
        return isBit ? $"{Code}{sep}{address}.{bit:D2}" : $"{Code}{sep}{address}";
    }
}

/// <summary>
/// Where one FINS area lives in the simulated memory: word n of the area ↔
/// table[Offset + n] for n in 0..Count-1 (word tables only). Areas without
/// a map are not served (end code 1101).
/// </summary>
public readonly record struct FinsMap(FinsArea Area, TableKind Table, int Offset, int Count)
{
    /// <summary>The default map: DM → words over the whole table. Every other area needs [[server.map]].</summary>
    public static FinsMap[] Defaults(IDeviceAccess device)
    {
        FinsArea.TryFromCode("DM", out var dm);
        return [new FinsMap(dm, TableKind.Words, 0, device.Length(TableKind.Words))];
    }

    /// <summary>Defaults plus user maps; a user map for DM replaces the default one.</summary>
    public static FinsMap[] Merge(IDeviceAccess device, IEnumerable<FinsMap> overrides)
    {
        var list = new List<FinsMap>(overrides);
        foreach (var d in Defaults(device))
        {
            if (!list.Any(m => m.Area.Code == d.Area.Code))
            {
                list.Add(d);
            }
        }
        return [.. list];
    }
}

/// <summary>FINS constants shared by the handler and the transports.</summary>
public static class Fins
{
    /// <summary>ICF, RSV, GCT, DNA, DA1, DA2, SNA, SA1, SA2, SID.</summary>
    public const int HeaderSize = 10;

    /// <summary>Largest FINS frame (header + command + data) moz accepts or produces.</summary>
    public const int FrameMax = 2000;

    /// <summary>Items per memory area read / write: words, bits (the 2000-byte frame), and items per multiple read.</summary>
    public const int WordsMax = 999;
    public const int BitsMax = 1998;
    public const int MultiItemsMax = 128;

    public const ushort CommandRead = 0x0101;
    public const ushort CommandWrite = 0x0102;
    public const ushort CommandReadMultiple = 0x0104;

    public const ushort EndOk = 0x0000;
    public const ushort EndCommand = 0x0401;          // undefined command
    public const ushort EndTooLong = 0x1001;          // command too long
    public const ushort EndTooShort = 0x1002;         // command too short
    public const ushort EndMismatch = 0x1003;         // number of items and data do not match
    public const ushort EndArea = 0x1101;             // no such / unmapped area
    public const ushort EndAddress = 0x1103;          // start address or bit designation out of range
    public const ushort EndRange = 0x1104;            // end address out of range
    public const ushort EndResponseTooLong = 0x110B;  // too many items for one response
    public const ushort EndParameter = 0x110C;        // parameter error (zero items)
    public const ushort EndReadOnly = 0x2101;         // write to a read-only table
}

/// <summary>What the server did with one request: the response frame plus the log fields.</summary>
public readonly record struct FinsOutcome(byte[] Response, string Op, string Target, int Points, ushort EndCode, bool ResponseRequired);

/// <summary>
/// Server half of FINS: one command frame (header + MRC/SRC + parameters)
/// in, memory operation through <see cref="IDeviceAccess"/>, response frame
/// out. Pure apart from the device access; no sockets, no transport framing.
/// Supported: memory area read 0101, write 0102 and multiple read 0104 on
/// the areas of <see cref="FinsArea"/>. Bit access reads / read-modify-writes
/// the containing words.
/// </summary>
public static class FinsServer
{
    /// <summary>
    /// Handle one frame. False when it is not a command frame at all (shorter
    /// than header + command, or ICF marks a response): the transport logs it
    /// as malformed. Everything else gets a response (end code on error).
    /// </summary>
    public static bool TryHandle(ReadOnlySpan<byte> frame, IDeviceAccess device, IReadOnlyList<FinsMap> maps, out FinsOutcome outcome)
    {
        outcome = default;
        if (frame.Length < Fins.HeaderSize + 2 || (frame[0] & 0x40) != 0)
        {
            return false;
        }
        var header = frame[..Fins.HeaderSize];
        bool responseRequired = (frame[0] & 0x01) == 0;
        int command = (frame[10] << 8) | frame[11];
        var body = frame[(Fins.HeaderSize + 2)..];
        outcome = command switch
        {
            Fins.CommandRead or Fins.CommandWrite => ReadWrite(header, command, body, device, maps, responseRequired),
            Fins.CommandReadMultiple => ReadMultiple(header, body, device, maps, responseRequired),
            _ => Error(header, command, Fins.EndCommand, $"cmd={command:X4}", "?", 0, responseRequired),
        };
        return true;
    }

    private static FinsOutcome ReadWrite(ReadOnlySpan<byte> header, int command, ReadOnlySpan<byte> body,
        IDeviceAccess device, IReadOnlyList<FinsMap> maps, bool responseRequired)
    {
        bool write = command == Fins.CommandWrite;
        var op = write ? "write" : "read";
        if (body.Length < 6)
        {
            return Error(header, command, Fins.EndTooShort, op, "?", 0, responseRequired);
        }
        byte code = body[0];
        int address = (body[1] << 8) | body[2];
        int bit = body[3];
        int count = (body[4] << 8) | body[5];
        if (!FinsArea.TryFromBinary(code, out var area, out var isBit))
        {
            return Error(header, command, Fins.EndArea, op, $"code={code:X2}", count, responseRequired);
        }
        var target = area.Format(address, bit, isBit);
        if (!TryFindMap(maps, area, out var map))
        {
            return Error(header, command, Fins.EndArea, op, target, count, responseRequired);
        }
        if (isBit ? bit > 15 : bit != 0)
        {
            return Error(header, command, Fins.EndAddress, op, target, count, responseRequired);
        }
        if (count == 0)
        {
            return Error(header, command, Fins.EndParameter, op, target, count, responseRequired);
        }
        if (count > (isBit ? Fins.BitsMax : Fins.WordsMax))
        {
            return Error(header, command, write ? Fins.EndTooLong : Fins.EndResponseTooLong, op, target, count, responseRequired);
        }
        int dataLength = isBit ? count : count * 2;
        if (write ? body.Length != 6 + dataLength : body.Length != 6)
        {
            return Error(header, command, write ? Fins.EndMismatch : Fins.EndTooLong, op, target, count, responseRequired);
        }
        if (address >= map.Count)
        {
            return Error(header, command, Fins.EndAddress, op, target, count, responseRequired);
        }
        int lastWord = isBit ? address + (bit + count - 1) / 16 : address + count - 1;
        if (lastWord >= map.Count)
        {
            return Error(header, command, Fins.EndRange, op, target, count, responseRequired);
        }
        if (write && map.Table is TableKind.ReadOnlyBits or TableKind.ReadOnlyWords)
        {
            return Error(header, command, Fins.EndReadOnly, op, target, count, responseRequired);
        }

        int index = map.Offset + address;
        var words = new ushort[lastWord - address + 1];
        device.Read(map.Table, index, words);
        if (!write)
        {
            var data = new byte[dataLength];
            if (isBit)
            {
                for (int i = 0; i < count; i++)
                {
                    int b = bit + i;
                    data[i] = (byte)((words[b / 16] >> (b % 16)) & 1);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    data[i * 2] = (byte)(words[i] >> 8);
                    data[i * 2 + 1] = (byte)words[i];
                }
            }
            return new FinsOutcome(Response(header, command, Fins.EndOk, data), op, target, count, Fins.EndOk, responseRequired);
        }

        var payload = body[6..];
        if (isBit)
        {
            for (int i = 0; i < count; i++)
            {
                int b = bit + i;
                int mask = 1 << (b % 16);
                words[b / 16] = (ushort)(payload[i] != 0 ? words[b / 16] | mask : words[b / 16] & ~mask);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                words[i] = (ushort)((payload[i * 2] << 8) | payload[i * 2 + 1]);
            }
        }
        device.Write(map.Table, index, words);
        return new FinsOutcome(Response(header, command, Fins.EndOk, []), op, target, count, Fins.EndOk, responseRequired);
    }

    private static FinsOutcome ReadMultiple(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body,
        IDeviceAccess device, IReadOnlyList<FinsMap> maps, bool responseRequired)
    {
        const string op = "read-multi";
        const int command = Fins.CommandReadMultiple;
        if (body.Length == 0)
        {
            return Error(header, command, Fins.EndParameter, op, "?", 0, responseRequired);
        }
        if (body.Length % 4 != 0)
        {
            return Error(header, command, Fins.EndMismatch, op, "?", body.Length / 4, responseRequired);
        }
        int items = body.Length / 4;
        if (items > Fins.MultiItemsMax)
        {
            return Error(header, command, Fins.EndResponseTooLong, op, "?", items, responseRequired);
        }
        var data = new List<byte>(items * 3);
        string? target = null;
        var one = new ushort[1];
        for (int i = 0; i < items; i++)
        {
            byte code = body[i * 4];
            int address = (body[i * 4 + 1] << 8) | body[i * 4 + 2];
            int bit = body[i * 4 + 3];
            if (!FinsArea.TryFromBinary(code, out var area, out var isBit))
            {
                return Error(header, command, Fins.EndArea, op, target ?? $"code={code:X2}", items, responseRequired);
            }
            target ??= area.Format(address, bit, isBit);
            if (!TryFindMap(maps, area, out var map))
            {
                return Error(header, command, Fins.EndArea, op, target, items, responseRequired);
            }
            if ((isBit ? bit > 15 : bit != 0) || address >= map.Count)
            {
                return Error(header, command, Fins.EndAddress, op, target, items, responseRequired);
            }
            device.Read(map.Table, map.Offset + address, one);
            data.Add(code);
            if (isBit)
            {
                data.Add((byte)((one[0] >> bit) & 1));
            }
            else
            {
                data.Add((byte)(one[0] >> 8));
                data.Add((byte)one[0]);
            }
        }
        return new FinsOutcome(Response(header, command, Fins.EndOk, data.ToArray()), op, target!, items, Fins.EndOk, responseRequired);
    }

    private static bool TryFindMap(IReadOnlyList<FinsMap> maps, FinsArea area, out FinsMap map)
    {
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].Area.Code == area.Code)
            {
                map = maps[i];
                return true;
            }
        }
        map = default;
        return false;
    }

    private static FinsOutcome Error(ReadOnlySpan<byte> header, int command, ushort endCode, string op, string target, int points, bool responseRequired) =>
        new(Response(header, command, endCode, []), op, target, points, endCode, responseRequired);

    /// <summary>Response header (ICF | 40, nodes swapped, GCT and SID echoed) | MRC/SRC | end code | data.</summary>
    private static byte[] Response(ReadOnlySpan<byte> header, int command, ushort endCode, ReadOnlySpan<byte> data)
    {
        var resp = new byte[Fins.HeaderSize + 4 + data.Length];
        resp[0] = (byte)(header[0] | 0x40);
        resp[1] = 0;
        resp[2] = header[2];
        header.Slice(6, 3).CopyTo(resp.AsSpan(3, 3)); // DNA/DA1/DA2 ← SNA/SA1/SA2
        header.Slice(3, 3).CopyTo(resp.AsSpan(6, 3)); // SNA/SA1/SA2 ← DNA/DA1/DA2
        resp[9] = header[9];
        resp[10] = (byte)(command >> 8);
        resp[11] = (byte)command;
        resp[12] = (byte)(endCode >> 8);
        resp[13] = (byte)endCode;
        data.CopyTo(resp.AsSpan(14));
        return resp;
    }
}
