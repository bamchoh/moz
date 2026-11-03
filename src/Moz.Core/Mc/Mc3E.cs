namespace Moz.Mc;

/// <summary>
/// One MC device type (Q/L-compatible 3E binary code): "D" 0xA8 word
/// decimal, "X" 0x9C bit hexadecimal, ... Parsed from the usual notation
/// ("D100", "M0", "X1A": the letters are the device, the digits the head
/// device number — hexadecimal for X/Y/B/W/SB/SW/DX/DY/ZR, decimal otherwise).
/// </summary>
public readonly record struct McDevice(string Code, byte Binary, bool IsBit, bool Hex)
{
    /// <summary>Head device numbers are 3 bytes on the wire.</summary>
    public const int AddressMax = 0xFFFFFF;

    private static readonly McDevice[] Table =
    [
        new("SM", 0x91, true, false), new("SD", 0xA9, false, false),
        new("X", 0x9C, true, true), new("Y", 0x9D, true, true),
        new("M", 0x90, true, false), new("L", 0x92, true, false),
        new("F", 0x93, true, false), new("V", 0x94, true, false),
        new("B", 0xA0, true, true), new("D", 0xA8, false, false),
        new("W", 0xB4, false, true),
        new("TS", 0xC1, true, false), new("TC", 0xC0, true, false), new("TN", 0xC2, false, false),
        new("SS", 0xC7, true, false), new("SC", 0xC6, true, false), new("SN", 0xC8, false, false),
        new("CS", 0xC4, true, false), new("CC", 0xC3, true, false), new("CN", 0xC5, false, false),
        new("SB", 0xA1, true, true), new("SW", 0xB5, false, true),
        new("S", 0x98, true, false),
        new("DX", 0xA2, true, true), new("DY", 0xA3, true, true),
        new("Z", 0xCC, false, false), new("R", 0xAF, false, false), new("ZR", 0xB0, false, true),
    ];

    /// <summary>Parse "D100" / "x1a" style notation; false for an unknown device, a bad number, or a number over 24 bits.</summary>
    public static bool TryParse(string text, out McDevice device, out int address)
    {
        device = default;
        address = 0;
        int letters = 0;
        while (letters < text.Length && char.IsAsciiLetter(text[letters]))
        {
            letters++;
        }
        if (letters == 0 || letters == text.Length)
        {
            return false;
        }
        var code = text[..letters].ToUpperInvariant();
        int found = Array.FindIndex(Table, d => d.Code == code);
        if (found < 0)
        {
            return false;
        }
        device = Table[found];
        var digits = text.AsSpan(letters);
        var style = device.Hex ? System.Globalization.NumberStyles.AllowHexSpecifier : System.Globalization.NumberStyles.None;
        if (!int.TryParse(digits, style, null, out address) || address < 0 || address > AddressMax)
        {
            device = default;
            address = 0;
            return false;
        }
        return true;
    }

    /// <summary>The notation for a head device number on this device ("D100", "X1A").</summary>
    public string Format(int address) => Hex ? $"{Code}{address:X}" : $"{Code}{address}";
}

/// <summary>3E frame routing (request destination) and the CPU monitoring timer (250 ms units).</summary>
public sealed record Mc3EOptions(byte Network = 0, byte Pc = 0xFF, ushort ModuleIo = 0x03FF, byte ModuleStation = 0, ushort TimerUnits = 0x10);

/// <summary>
/// MC protocol, 3E frame, binary code: pure request building and response
/// parsing (no sockets). Request: subheader 5000, routing (network, PC,
/// module I/O, station), data length, monitoring timer, command, subcommand,
/// head device (3 bytes), device code, points, [write data]. Response:
/// subheader D000, routing, data length, end code, [read data]. Multi-byte
/// fields are little-endian. Batch read (0401) and batch write (1401) in
/// word units (subcommand 0000) for word devices and bit units (0001) for
/// bit devices; bit data packs two points per byte, high nibble first.
/// </summary>
public static class Mc3E
{
    public const int HeaderSize = 9;
    public const int ReadWordsMax = 960;
    public const int ReadBitsMax = 7168;
    public const int WriteWordsMax = 960;
    public const int WriteBitsMax = 7168;

    private const ushort CommandBatchRead = 0x0401;
    private const ushort CommandBatchWrite = 0x1401;
    private const int FixedRequestData = 12; // timer(2) + command(2) + subcommand(2) + head(3) + code(1) + points(2)

    /// <summary>Monitoring timer units (250 ms) for a request timeout; at least 1, at most 0xFFFF.</summary>
    public static ushort TimerUnitsFor(int timeoutMs) => (ushort)Math.Clamp(timeoutMs / 250, 1, ushort.MaxValue);

    /// <summary>Batch read request for <paramref name="count"/> points from <paramref name="device"/> at <paramref name="address"/>.</summary>
    public static byte[] BuildReadRequest(Mc3EOptions options, McDevice device, int address, int count)
    {
        CheckRange(device, address, count, device.IsBit ? ReadBitsMax : ReadWordsMax);
        var req = new byte[HeaderSize + FixedRequestData];
        WriteCommand(req, options, CommandBatchRead, device, address, count, FixedRequestData);
        return req;
    }

    /// <summary>Batch write request (word units) for a word device.</summary>
    public static byte[] BuildWriteRequest(Mc3EOptions options, McDevice device, int address, ReadOnlySpan<ushort> words)
    {
        if (device.IsBit)
        {
            throw new ArgumentException($"{device.Code} is a bit device: write bits, not words", nameof(device));
        }
        CheckRange(device, address, words.Length, WriteWordsMax);
        var req = new byte[HeaderSize + FixedRequestData + words.Length * 2];
        WriteCommand(req, options, CommandBatchWrite, device, address, words.Length, FixedRequestData + words.Length * 2);
        int at = HeaderSize + FixedRequestData;
        foreach (var w in words)
        {
            req[at++] = (byte)w;
            req[at++] = (byte)(w >> 8);
        }
        return req;
    }

    /// <summary>Batch write request (bit units) for a bit device.</summary>
    public static byte[] BuildWriteRequest(Mc3EOptions options, McDevice device, int address, ReadOnlySpan<bool> bits)
    {
        if (!device.IsBit)
        {
            throw new ArgumentException($"{device.Code} is a word device: write words, not bits", nameof(device));
        }
        CheckRange(device, address, bits.Length, WriteBitsMax);
        int dataLength = (bits.Length + 1) / 2;
        var req = new byte[HeaderSize + FixedRequestData + dataLength];
        WriteCommand(req, options, CommandBatchWrite, device, address, bits.Length, FixedRequestData + dataLength);
        int at = HeaderSize + FixedRequestData;
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                req[at + i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
            }
        }
        return req;
    }

    /// <summary>
    /// Parse the 9-byte response header: subheader D000, routing echo, data
    /// length (end code + data). False when the subheader is wrong or the
    /// length cannot hold an end code.
    /// </summary>
    public static bool TryParseResponseHeader(ReadOnlySpan<byte> header, out int dataLength)
    {
        dataLength = 0;
        if (header.Length < HeaderSize || header[0] != 0xD0 || header[1] != 0x00)
        {
            return false;
        }
        dataLength = header[7] | (header[8] << 8);
        return dataLength >= 2;
    }

    /// <summary>The end code at the start of a response body (0 = success).</summary>
    public static ushort EndCode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2)
        {
            throw new ArgumentException($"response body too short for an end code ({body.Length} bytes)", nameof(body));
        }
        return (ushort)(body[0] | (body[1] << 8));
    }

    /// <summary>Read data of a word-unit batch read: end code + count little-endian words.</summary>
    public static ushort[] ParseWords(ReadOnlySpan<byte> body, int count)
    {
        if (body.Length != 2 + count * 2)
        {
            throw new ArgumentException($"response length mismatch: {body.Length} bytes for {count} words", nameof(body));
        }
        var words = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            words[i] = (ushort)(body[2 + i * 2] | (body[3 + i * 2] << 8));
        }
        return words;
    }

    /// <summary>Read data of a bit-unit batch read: end code + two points per byte (high nibble first).</summary>
    public static bool[] ParseBits(ReadOnlySpan<byte> body, int count)
    {
        if (body.Length != 2 + (count + 1) / 2)
        {
            throw new ArgumentException($"response length mismatch: {body.Length} bytes for {count} bits", nameof(body));
        }
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
        {
            bits[i] = (body[2 + i / 2] & (i % 2 == 0 ? 0x10 : 0x01)) != 0;
        }
        return bits;
    }

    private static void CheckRange(McDevice device, int address, int count, int max)
    {
        if (count < 1 || count > max)
        {
            throw new ArgumentException($"count {count} is out of range 1..{max} for {device.Code}", nameof(count));
        }
        if (address < 0 || address + count > McDevice.AddressMax + 1)
        {
            throw new ArgumentException($"{device.Format(address)} + {count} exceeds the 24-bit device address range", nameof(address));
        }
    }

    private static void WriteCommand(byte[] req, Mc3EOptions o, ushort command, McDevice device, int address, int count, int dataLength)
    {
        req[0] = 0x50;
        req[1] = 0x00;
        req[2] = o.Network;
        req[3] = o.Pc;
        req[4] = (byte)o.ModuleIo;
        req[5] = (byte)(o.ModuleIo >> 8);
        req[6] = o.ModuleStation;
        req[7] = (byte)dataLength;
        req[8] = (byte)(dataLength >> 8);
        req[9] = (byte)o.TimerUnits;
        req[10] = (byte)(o.TimerUnits >> 8);
        req[11] = (byte)command;
        req[12] = (byte)(command >> 8);
        req[13] = (byte)(device.IsBit ? 0x01 : 0x00); // subcommand: bit units / word units
        req[14] = 0x00;
        req[15] = (byte)address;
        req[16] = (byte)(address >> 8);
        req[17] = (byte)(address >> 16);
        req[18] = device.Binary;
        req[19] = (byte)count;
        req[20] = (byte)(count >> 8);
    }
}
