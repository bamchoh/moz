using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.Modbus.Rtu;

namespace Moz.Tests;

/// <summary>Starts an RTU server over a caller-provided Stream (no serial needed).</summary>
internal static class RtuTestServer
{
    public static async Task<TestServer> Start(Stream stream, Device device,
        ushort[]? unitIds = null, CapturingProvider? provider = null)
    {
        var server = new RtuServer("rtu-test", stream, device, unitIds,
            idleGapMs: 50, provider is null ? NullLogger.Instance : provider.CreateLogger("test"));
        var cts = new CancellationTokenSource();
        _ = server.RunAsync(cts.Token);
        // no async bind phase for a given stream; give the loop a moment and assume ready
        await Task.Delay(30);
        return new TestServer(device, 0, cts, provider);
    }
}

/// <summary>
/// RTU layer, from the inside out, without any serial hardware:
/// 1. CRC16 vectors (independent, well-known Modbus examples)
/// 2. ADU framing (encode + parse, byte-exact)
/// 3. Full duplex round trip over an in-memory pipe pair (server ⇄ test master)
/// </summary>
public class RtuTests
{
    // --- CRC16 ---------------------------------------------------------------

    [Theory]
    [InlineData("010300000001", "840A")]   // unit 1, read 1 holding register at addr 0
    [InlineData("1103006B0003", "7687")]   // Modicon example: unit 17, FC03 addr 107 qty 3
    [InlineData("010402FFFF", "B880")]     // response: unit 1, FC04, 1 reg = 0xFFFF
    public void Crc16_KnownVectors(string frameHex, string expectedLsbFirstWireHex)
    {
        ushort crc = Crc16.Compute(Hex(frameHex));
        // on the wire the CRC travels LSB first
        Assert.Equal(expectedLsbFirstWireHex, $"{(crc & 0xFF):X2}{(crc >> 8):X2}");
    }

    [Fact]
    public void Crc16_EmptyMessage()
    {
        Assert.Equal(0xFFFF, Crc16.Compute(ReadOnlySpan<byte>.Empty));
    }

    /// <summary>The textual definition (poly 0xA001, init 0xFFFF, LSB-first) computed
    /// by hand as an independent cross-check of the fast implementation.</summary>
    [Fact]
    public void Crc16_MatchesDefinition()
    {
        var frame = Hex("1103006B0003");
        ushort crc = 0xFFFF;
        foreach (var b in frame)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }
        Assert.Equal(crc, Crc16.Compute(frame));
    }

    // --- framing ---------------------------------------------------------------

    [Fact]
    public void TryParseFrame_KnownRequestFrame()
    {
        var frame = Hex("1103006B00037687");
        Assert.True(Rtu.TryParseFrame(frame, out var addr, out var pdu));
        Assert.Equal(0x11, addr);
        Assert.Equal("03006B0003", ToHex(pdu));
    }

    [Fact]
    public void Frame_ThenParse_RoundTrip()
    {
        var pdu = Hex("0302010203");
        var frame = Rtu.Frame(0x11, pdu);
        Assert.Equal(1 + pdu.Length + 2, frame.Length);
        Assert.True(Rtu.TryParseFrame(frame, out var addr, out var parsed));
        Assert.Equal(0x11, addr);
        Assert.Equal(ToHex(pdu), ToHex(parsed));
    }

    [Fact]
    public void TryParseFrame_MultipleWriteRequest_FC16_And_FC15()
    {
        // FC16: addr(1) fc(1) start(2) qty(2) byteCount(1) data(byteCount) crc(2) = 9 + byteCount
        var pdu16 = Hex("10001400020411112222");
        var frame = Rtu.Frame(0x01, pdu16);
        Assert.Equal(13, frame.Length);
        Assert.True(Rtu.TryParseFrame(frame, out var addr, out var parsed));
        Assert.Equal(0x01, addr);
        Assert.Equal(ToHex(pdu16), ToHex(parsed));
        Assert.False(Rtu.TryParseFrame(frame.AsSpan(0, frame.Length - 1), out _, out _)); // one byte short

        // FC15: 10 coils = 2 data bytes (spec example 0F 0013 000A 02 CD01)
        var pdu15 = Hex("0F0013000A02CD01");
        Assert.True(Rtu.TryParseFrame(Rtu.Frame(0x11, pdu15), out addr, out parsed));
        Assert.Equal(0x11, addr);
        Assert.Equal(ToHex(pdu15), ToHex(parsed));
    }

    [Fact]
    public void TryParseFrame_BadCrc_Rejected()
    {
        Assert.False(Rtu.TryParseFrame(Hex("1103006B00037686"), out _, out _)); // last byte flipped
        Assert.False(Rtu.TryParseFrame(Hex("1103006B00030000"), out _, out _)); // zeroed CRC
    }

    [Fact]
    public void TryParseFrame_CompleteFramePlusGarbage_Rejected()
    {
        // anything after a complete frame is garbage: the accumulated buffer is discarded
        Assert.False(Rtu.TryParseFrame(Hex("1103006B00037687FF"), out _, out _));
    }

    [Fact]
    public void TryParseFrame_PartialFrames_KeepReading()
    {
        Assert.False(Rtu.TryParseFrame(Hex("11"), out _, out _));
        Assert.False(Rtu.TryParseFrame(Hex("1103006B00"), out _, out _)); // no CRC yet
        Assert.False(Rtu.TryParseFrame(Hex("1103006B0003"), out _, out _)); // reads want 8 bytes
    }

    [Fact]
    public void TryParseFrame_UnknownFc_NotParseable()
    {
        // unknown FCs keep accumulating (only the idle gap ends such frames)
        Assert.False(Rtu.TryParseFrame(Hex("11090000018776"), out _, out _));
    }

    // --- response framing (master side) ------------------------------------------------

    [Theory]
    [InlineData("", Rtu.NeedMore)]
    [InlineData("11", Rtu.NeedMore)]
    [InlineData("1103", Rtu.NeedMore)]          // read: byte count not seen yet
    [InlineData("110306", 11)]                  // FC03, 6 data bytes: 1+1+1+6+2
    [InlineData("0101", Rtu.NeedMore)]
    [InlineData("010101", 6)]                   // FC01, 1 data byte
    [InlineData("0105", 8)]                     // write echoes are fixed-length
    [InlineData("0106", 8)]
    [InlineData("010F", 8)]
    [InlineData("0110", 8)]
    [InlineData("0183", 5)]                     // exception: addr + fc|80 + code + crc
    [InlineData("0190", 5)]
    [InlineData("0107", Rtu.Invalid)]           // FC the master never sends
    [InlineData("0100", Rtu.Invalid)]
    public void ResponseLength_FromFcAndByteCount(string hex, int expected)
    {
        Assert.Equal(expected, Rtu.ResponseLength(Hex(hex)));
    }

    // --- duplex round trip (no serial, no virtual COM) ----------------------------

    [Fact]
    public async Task DuplexRoundTrip_ReadRegisters()
    {
        var device = new Device("t", holdingRegisters: 200);
        device.Seed(TableKind.HoldingRegister, 107, new ushort[] { 0xC001 });
        await using var bus = new SerialBus();
        await using var server = await RtuTestServer.Start(bus.SlaveStream, device);
        using var master = bus.MasterStream;

        // The Modicon example reads qty=3: device has 0xC001 at 107, zeros after.
        await master.WriteAsync(Hex("1103006B00037687"));
        var resp = await ReadFrame(master);

        Assert.Equal(11, resp.Length); // addr + fc + bc(6) + 3 words + crc(2)
        Assert.Equal(0x11, resp[0]);
        Assert.Equal(0x03, resp[1]);
        Assert.Equal(6, resp[2]);
        Assert.Equal(0xC001, (ushort)((resp[3] << 8) | resp[4]));      // first word = seeded
        Assert.Equal(0, (resp[5] << 8) | resp[6]);                      // second = 0
        Assert.Equal(0, (resp[7] << 8) | resp[8]);                      // third = 0
        Assert.True(Rtu.CrcOk(resp));                                    // response CRC valid
    }

    [Fact]
    public async Task DuplexRoundTrip_WriteThenRead()
    {
        var device = new Device("t", holdingRegisters: 100);
        await using var bus = new SerialBus();
        await using var server = await RtuTestServer.Start(bus.SlaveStream, device);
        using var master = bus.MasterStream;

        // FC06: write 0x1234 to holding 5 (unit 1), then read it back
        await master.WriteAsync(Rtu.Frame(0x01, Hex("0600051234")));
        var echo = await ReadFrame(master);
        Assert.True(Rtu.TryParseFrame(echo, out var addr, out var pdu));
        Assert.Equal(0x01, addr);
        Assert.Equal("0600051234", ToHex(pdu));

        Span<ushort> w = stackalloc ushort[1];
        device.Read(TableKind.HoldingRegister, 5, w);
        Assert.Equal((ushort)0x1234, w[0]);

        await master.WriteAsync(Rtu.Frame(0x01, Hex("0300050001")));
        var resp = await ReadFrame(master);
        Assert.Equal(7, resp.Length); // addr + fc + bc(2) + 1 word + crc(2)
        Assert.Equal(0x01, resp[0]);
        Assert.Equal(0x03, resp[1]);
        Assert.Equal(0x1234, (ushort)((resp[3] << 8) | resp[4]));
        Assert.True(Rtu.CrcOk(resp));
    }

    [Fact]
    public async Task BadCrcRequest_GetsNoAnswer()
    {
        var device = new Device("t", holdingRegisters: 100);
        await using var bus = new SerialBus();
        await using var server = await RtuTestServer.Start(bus.SlaveStream, device);
        using var master = bus.MasterStream;

        await master.WriteAsync(Hex("1103006B00037686")); // bad CRC
        var resp = await ReadFrame(master, idleMs: 400);
        Assert.Empty(resp); // spec: a bad-CRC frame is silently discarded
    }

    [Fact]
    public async Task ForeignSlaveAddress_GetsNoAnswer()
    {
        var device = new Device("t", holdingRegisters: 100);
        await using var bus = new SerialBus();
        await using var server = await RtuTestServer.Start(bus.SlaveStream, device, unitIds: [1]);
        using var master = bus.MasterStream;

        await master.WriteAsync(Hex("1103006B00037687")); // unit 17, served is 1
        var resp = await ReadFrame(master, idleMs: 400);
        Assert.Empty(resp);

        await master.WriteAsync(Rtu.Frame(0x01, Hex("03006B0001"))); // unit 1: served
        var resp2 = await ReadFrame(master);
        Assert.Equal(0x01, resp2[0]);
    }

    /// <summary>Read bytes until the idle window ends (frames are short and bounded in tests).</summary>
    private static async Task<byte[]> ReadFrame(Stream s, int idleMs = 300)
    {
        var buf = new byte[300];
        var total = 0;
        while (total < 260)
        {
            using var idle = new CancellationTokenSource(idleMs);
            try
            {
                var n = await s.ReadAsync(buf.AsMemory(total), idle.Token);
                if (n == 0)
                {
                    break; // peer closed
                }
                total += n;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return buf[..total];
    }

    private static byte[] Hex(string s)
    {
        s = s.Replace(" ", "");
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private static string ToHex(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in data)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}