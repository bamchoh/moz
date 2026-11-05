using System.Globalization;
using System.Text;
using Moz.Devices;

namespace Moz.Kv;

/// <summary>Value format suffix of a word device: .U (unsigned 16, default), .S (signed 16), .D (unsigned 32), .L (signed 32), .H (hex 16).</summary>
public enum KvFormat
{
    U,
    S,
    D,
    L,
    H,
}

/// <summary>
/// One KV device type. Word devices (DM, EM, FM, W, ZF) are numbered in
/// decimal words. Relays (R, MR, LR) use channel × 100 + bit (R1015 =
/// channel 10, bit 15; bits 00..15); B is hexadecimal with 16 bits per
/// channel (B1F = channel 1, bit 15).
/// </summary>
public readonly record struct KvDevice(string Code, bool IsBit, bool Hex)
{
    private static readonly KvDevice[] Table =
    [
        new("DM", false, false), new("EM", false, false), new("FM", false, false), new("W", false, false), new("ZF", false, false),
        new("R", true, false), new("MR", true, false), new("LR", true, false), new("B", true, true),
    ];

    public static bool TryFromCode(string text, out KvDevice device)
    {
        var upper = text.Trim().ToUpperInvariant();
        int found = Array.FindIndex(Table, d => d.Code == upper);
        device = found < 0 ? default : Table[found];
        return found >= 0;
    }

    /// <summary>
    /// Parse "DM100", "dm100.s", "R1015", "B1F": device, channel / word
    /// address, bit (0 for words) and format (U unless given; relays take
    /// none). False for anything else.
    /// </summary>
    public static bool TryParse(string text, out KvDevice device, out int address, out int bit, out KvFormat format)
    {
        device = default;
        address = 0;
        bit = 0;
        format = KvFormat.U;
        var upper = text.Trim().ToUpperInvariant();
        int dot = upper.IndexOf('.');
        var name = dot < 0 ? upper : upper[..dot];
        int letters = 0;
        while (letters < name.Length && char.IsAsciiLetter(name[letters]))
        {
            letters++;
        }
        if (letters == 0 || letters == name.Length || !TryFromCode(name[..letters], out device))
        {
            return false;
        }
        var digits = name.AsSpan(letters);
        var style = device.Hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        if (!int.TryParse(digits, style, CultureInfo.InvariantCulture, out int number) || number < 0)
        {
            return false;
        }
        if (dot >= 0)
        {
            if (device.IsBit)
            {
                return false; // relays carry no format
            }
            var suffix = upper[(dot + 1)..];
            if (suffix.Length != 1 || !Enum.TryParse(suffix, out format))
            {
                return false;
            }
        }
        if (!device.IsBit)
        {
            address = number;
            return true;
        }
        if (device.Hex)
        {
            address = number / 16;
            bit = number % 16;
            return true;
        }
        address = number / 100;
        bit = number % 100;
        return bit <= 15;
    }

    /// <summary>Log notation: "DM100", "R1015", "B1F".</summary>
    public string Format(int address, int bit) => !IsBit
        ? $"{Code}{address}"
        : Hex ? $"{Code}{address * 16 + bit:X}" : $"{Code}{address * 100 + bit}";
}

/// <summary>
/// Where one KV device type lives in the simulated memory: word n ↔
/// table[Offset + n]; relay channel c bit b ↔ bit table[Offset + c*16 + b].
/// Devices without a map are not served (E0).
/// </summary>
public readonly record struct KvMap(KvDevice Device, TableKind Table, int Offset, int Count)
{
    /// <summary>The default map: DM → words and R → bits, each over the whole table. Everything else needs [[server.map]].</summary>
    public static KvMap[] Defaults(IDeviceAccess device)
    {
        KvDevice.TryFromCode("DM", out var dm);
        KvDevice.TryFromCode("R", out var r);
        return
        [
            new KvMap(dm, TableKind.Words, 0, device.Length(TableKind.Words)),
            new KvMap(r, TableKind.Bits, 0, device.Length(TableKind.Bits)),
        ];
    }

    /// <summary>Defaults plus user maps; a user map for DM / R replaces the default one.</summary>
    public static KvMap[] Merge(IDeviceAccess device, IEnumerable<KvMap> overrides)
    {
        var list = new List<KvMap>(overrides);
        foreach (var d in Defaults(device))
        {
            if (!list.Any(m => m.Device.Code == d.Device.Code))
            {
                list.Add(d);
            }
        }
        return [.. list];
    }
}

/// <summary>What the server did with one line: the response (without CR LF; null = no answer) plus the log fields.</summary>
public readonly record struct KvOutcome(string? Response, string Op, string Target, int Points, string? Error);

/// <summary>
/// Server half of the Keyence KV host link (ASCII, one command per line):
/// CR / CQ (connect / disconnect), ?K / ?M (model / mode), RD / RDS (read),
/// WR / WRS (write), ST / RS (set / reset a relay). Word values follow the
/// .U/.S/.D/.L/.H format (32-bit values are low word first). Errors: E0
/// device (unknown, out of range, bad format, unmapped), E1 command
/// (unknown, wrong arguments, bad count or value), E4 write-protected.
/// Pure apart from the device access; no sockets.
/// </summary>
public static class KvHostLink
{
    /// <summary>Longest request line accepted by the transport (bytes, without the terminator).</summary>
    public const int LineMax = 4096;

    public const int CountMax = 1000;

    /// <summary>Answer of ?K (KV-7500 model code) and ?M (1 = RUN).</summary>
    public const string ModelCode = "55";
    public const string ModeRun = "1";

    public const string ErrorDevice = "E0";
    public const string ErrorCommand = "E1";
    public const string ErrorProtected = "E4";

    public static KvOutcome Handle(string line, IDeviceAccess device, IReadOnlyList<KvMap> maps)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return new KvOutcome(null, "", "", 0, null);
        }
        var command = parts[0].ToUpperInvariant();
        switch (command)
        {
            case "CR":
                return new KvOutcome("CC", "ctrl", command, 0, null);
            case "CQ":
                return new KvOutcome("CF", "ctrl", command, 0, null);
            case "?K":
                return new KvOutcome(ModelCode, "ctrl", command, 0, null);
            case "?M":
                return new KvOutcome(ModeRun, "ctrl", command, 0, null);
            case "RD":
            case "RDS":
                return Read(command, parts, device, maps);
            case "WR":
            case "WRS":
                return Write(command, parts, device, maps);
            case "ST":
            case "RS":
                return SetReset(command, parts, device, maps);
            default:
                return Error(ErrorCommand, $"cmd={command}", "?", 0);
        }
    }

    private static KvOutcome Read(string command, string[] parts, IDeviceAccess device, IReadOnlyList<KvMap> maps)
    {
        const string op = "read";
        bool multi = command == "RDS";
        if (parts.Length != (multi ? 3 : 2))
        {
            return Error(ErrorCommand, op, parts.Length > 1 ? parts[1] : "?", 0);
        }
        if (!TryTarget(parts[1], maps, out var dev, out var address, out var bit, out var format, out var map, out var target))
        {
            return Error(ErrorDevice, op, target, 0);
        }
        int count = 1;
        if (multi && !TryCount(parts[2], out count))
        {
            return Error(ErrorCommand, op, target, 0);
        }
        int width = Width(format);
        if (!InRange(map, dev, address, bit, count, width))
        {
            return Error(ErrorDevice, op, target, count);
        }
        var sb = new StringBuilder();
        if (dev.IsBit)
        {
            var bits = new bool[count];
            device.Read(map.Table, map.Offset + address * 16 + bit, bits);
            foreach (var b in bits)
            {
                sb.Append(sb.Length > 0 ? " " : "").Append(b ? '1' : '0');
            }
        }
        else
        {
            var words = new ushort[count * width];
            device.Read(map.Table, map.Offset + address, words);
            for (int i = 0; i < count; i++)
            {
                sb.Append(sb.Length > 0 ? " " : "").Append(FormatValue(words, i * width, format));
            }
        }
        return new KvOutcome(sb.ToString(), op, target, count, null);
    }

    private static KvOutcome Write(string command, string[] parts, IDeviceAccess device, IReadOnlyList<KvMap> maps)
    {
        const string op = "write";
        bool multi = command == "WRS";
        if (parts.Length < 3)
        {
            return Error(ErrorCommand, op, parts.Length > 1 ? parts[1] : "?", 0);
        }
        if (!TryTarget(parts[1], maps, out var dev, out var address, out var bit, out var format, out var map, out var target))
        {
            return Error(ErrorDevice, op, target, 0);
        }
        int count = 1;
        int firstValue = 2;
        if (multi)
        {
            if (!TryCount(parts[2], out count))
            {
                return Error(ErrorCommand, op, target, 0);
            }
            firstValue = 3;
        }
        if (parts.Length != firstValue + count)
        {
            return Error(ErrorCommand, op, target, count);
        }
        int width = Width(format);
        if (!InRange(map, dev, address, bit, count, width))
        {
            return Error(ErrorDevice, op, target, count);
        }
        if (dev.IsBit)
        {
            var bits = new bool[count];
            for (int i = 0; i < count; i++)
            {
                if (parts[firstValue + i] is not ("0" or "1"))
                {
                    return Error(ErrorCommand, op, target, count);
                }
                bits[i] = parts[firstValue + i] == "1";
            }
            if (map.Table.IsReadOnly())
            {
                return Error(ErrorProtected, op, target, count);
            }
            device.Write(map.Table, map.Offset + address * 16 + bit, bits);
        }
        else
        {
            var words = new ushort[count * width];
            for (int i = 0; i < count; i++)
            {
                if (!TryParseValue(parts[firstValue + i], format, words, i * width))
                {
                    return Error(ErrorCommand, op, target, count);
                }
            }
            if (map.Table.IsReadOnly())
            {
                return Error(ErrorProtected, op, target, count);
            }
            device.Write(map.Table, map.Offset + address, words);
        }
        return new KvOutcome("OK", op, target, count, null);
    }

    private static KvOutcome SetReset(string command, string[] parts, IDeviceAccess device, IReadOnlyList<KvMap> maps)
    {
        var op = command == "ST" ? "set" : "reset";
        if (parts.Length != 2)
        {
            return Error(ErrorCommand, op, parts.Length > 1 ? parts[1] : "?", 0);
        }
        if (!TryTarget(parts[1], maps, out var dev, out var address, out var bit, out _, out var map, out var target) || !dev.IsBit)
        {
            return Error(ErrorDevice, op, target, 0);
        }
        if (!InRange(map, dev, address, bit, 1, 1))
        {
            return Error(ErrorDevice, op, target, 1);
        }
        if (map.Table.IsReadOnly())
        {
            return Error(ErrorProtected, op, target, 1);
        }
        device.WriteBit(map.Table, map.Offset + address * 16 + bit, command == "ST");
        return new KvOutcome("OK", op, target, 1, null);
    }

    /// <summary>Parse the device argument and find its map; target is the log notation (or the raw text when unparseable).</summary>
    private static bool TryTarget(string text, IReadOnlyList<KvMap> maps, out KvDevice dev, out int address, out int bit,
        out KvFormat format, out KvMap map, out string target)
    {
        map = default;
        if (!KvDevice.TryParse(text, out dev, out address, out bit, out format))
        {
            target = text;
            return false;
        }
        target = dev.Format(address, bit);
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].Device.Code == dev.Code)
            {
                map = maps[i];
                return true;
            }
        }
        return false;
    }

    private static bool TryCount(string text, out int count) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 1 && count <= CountMax;

    private static int Width(KvFormat format) => format is KvFormat.D or KvFormat.L ? 2 : 1;

    private static bool InRange(KvMap map, KvDevice dev, int address, int bit, int count, int width)
    {
        long first = dev.IsBit ? (long)address * 16 + bit : address;
        long cells = dev.IsBit ? count : (long)count * width;
        return first < map.Count && first + cells <= map.Count;
    }

    private static string FormatValue(ushort[] words, int at, KvFormat format) => format switch
    {
        KvFormat.U => words[at].ToString(CultureInfo.InvariantCulture),
        KvFormat.S => ((short)words[at]).ToString(CultureInfo.InvariantCulture),
        KvFormat.D => ((uint)words[at] | ((uint)words[at + 1] << 16)).ToString(CultureInfo.InvariantCulture),
        KvFormat.L => ((int)((uint)words[at] | ((uint)words[at + 1] << 16))).ToString(CultureInfo.InvariantCulture),
        _ => words[at].ToString("X4", CultureInfo.InvariantCulture),
    };

    private static bool TryParseValue(string text, KvFormat format, ushort[] words, int at)
    {
        const NumberStyles signed = NumberStyles.AllowLeadingSign;
        switch (format)
        {
            case KvFormat.U:
                if (!long.TryParse(text, signed, CultureInfo.InvariantCulture, out var u) || u < 0 || u > ushort.MaxValue) return false;
                words[at] = (ushort)u;
                return true;
            case KvFormat.S:
                if (!long.TryParse(text, signed, CultureInfo.InvariantCulture, out var s) || s < short.MinValue || s > short.MaxValue) return false;
                words[at] = (ushort)(short)s;
                return true;
            case KvFormat.D:
                if (!long.TryParse(text, signed, CultureInfo.InvariantCulture, out var d) || d < 0 || d > uint.MaxValue) return false;
                words[at] = (ushort)d;
                words[at + 1] = (ushort)(d >> 16);
                return true;
            case KvFormat.L:
                if (!long.TryParse(text, signed, CultureInfo.InvariantCulture, out var l) || l < int.MinValue || l > int.MaxValue) return false;
                words[at] = (ushort)(uint)l;
                words[at + 1] = (ushort)((uint)l >> 16);
                return true;
            default:
                if (text.Length is 0 or > 4 || !ushort.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var h)) return false;
                words[at] = h;
                return true;
        }
    }

    private static KvOutcome Error(string code, string op, string target, int points) => new(code, op, target, points, code);
}
