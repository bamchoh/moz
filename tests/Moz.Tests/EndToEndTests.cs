using System.Net;
using Microsoft.Extensions.Logging;
using ModbusEndianness = FluentModbus.ModbusEndianness;
using ModbusTcpClient = FluentModbus.ModbusTcpClient;
using ModbusException = FluentModbus.ModbusException;
using Moz.Devices;
using Moz.Modbus.Tcp;

namespace Moz.Tests;

/// <summary>
/// Real-traffic tests: FluentModbus client ⇄ ModbusTcpServer on an ephemeral
/// port. Asserts memory effects and the structured comm log, plus the raw
/// connection-level policies (malformed frames close, disabled unit ids
/// answer nothing).
/// </summary>
public class EndToEndTests
{
    private sealed record Captured(LogLevel Level, int EventId, string Message);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public readonly List<Captured> Entries = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);
        public void Dispose() { }

        private sealed class CapturingLogger(List<Captured> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add(new Captured(logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    /// <summary>Start a server on port 0 and return its assigned port + cancel source.</summary>
    private static async Task<(ushort Port, CancellationTokenSource Cts)>
        StartServerAsync(Device device, ushort[]? unitIds = null, CapturingProvider? provider = null)
    {
        var logger = provider is null
            ? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            : provider.CreateLogger("test");
        var server = new ModbusTcpServer("test", new IPEndPoint(IPAddress.Loopback, 0), device, unitIds, logger);
        var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);
        while (server.BoundEndpoint is null)
        {
            await Task.Delay(10);
            if (run.IsFaulted)
            {
                await run; // rethrow
            }
        }
        return ((ushort)((IPEndPoint)server.BoundEndpoint).Port, cts);
    }

    // NOTE: keep this in sync with CommLog.RequestState's rendering.
    private const string RequestLogLine = "conn=c1 unit=3 fc=06 addr=5 qty=1 result=ok";

    private static int BitAt(ReadOnlySpan<byte> data, int bitIndex) => (data[bitIndex / 8] >> (bitIndex % 8)) & 1;

    /// <summary>One raw MBAP round-trip against a live server; returns the response frame.</summary>
    private static async Task<byte[]> SendFrameAsync(ushort port, byte[] frame)
    {
        using var raw = new System.Net.Sockets.TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var stream = raw.GetStream();
        await stream.WriteAsync(frame);
        var buf = new byte[300];
        using var timeout = new CancellationTokenSource(5000);
        var n = await stream.ReadAsync(buf, timeout.Token);
        return buf[..n];
    }

    [Fact]
    public async Task FullRoundTrip_AllFCs()
    {
        var device = new Device("t", coils: 2000, discreteInputs: 2000, holdingRegisters: 200, inputRegisters: 200);
        device.Seed(TableKind.HoldingRegister, 0, (ushort[])[0x1234, 0xABCD]);
        device.Seed(TableKind.DiscreteInput, 0, (bool[])[true, false, true]);
        device.Seed(TableKind.InputRegister, 0, (ushort[])[0xBEEF]);
        var (port, cts) = await StartServerAsync(device);

        try
        {
            using var client = new ModbusTcpClient();
            client.Connect(new IPEndPoint(IPAddress.Loopback, port), ModbusEndianness.BigEndian);

            // FC03 — read holding registers
            var holding = client.ReadHoldingRegisters<ushort>(1, 0, 2);
            Assert.Equal((ushort)0x1234, holding[0]);
            Assert.Equal((ushort)0xABCD, holding[1]);

            // FC04 — read input registers
            var inputs = client.ReadInputRegisters<ushort>(1, 0, 1);
            Assert.Equal((ushort)0xBEEF, inputs[0]);

            // FC05 + FC01 — write and read coils (FluentModbus returns packed bits as bytes)
            client.WriteSingleCoil(1, 0, true);
            client.WriteSingleCoil(1, 1, false);
            var coils = client.ReadCoils(1, 0, 3);
            Assert.Equal(1, coils.Length);
            Assert.Equal(0b001, BitAt(coils, 0) | BitAt(coils, 1) << 1 | BitAt(coils, 2) << 2);
            // bit layout: bit0=coil0=true, bit1=coil1=false, bit2=coil2=false

            // FC02 — read discrete inputs (seeded true, false, true)
            var discretes = client.ReadDiscreteInputs(1, 0, 3);
            Assert.Equal(1, discretes.Length);
            Assert.Equal(0b101, BitAt(discretes, 0) | BitAt(discretes, 1) << 1 | BitAt(discretes, 2) << 2);

            // FC06 — write single register
            client.WriteSingleRegister(1, 10, 0x6789);
            Assert.Equal((ushort)0x6789, client.ReadHoldingRegisters<ushort>(1, 10, 1)[0]);

            // FC16 — write multiple registers
            client.WriteMultipleRegisters(1, 20, (ushort[])[1, 2, 3]);
            var multi = client.ReadHoldingRegisters<ushort>(1, 20, 3);
            Assert.Equal((ushort)1, multi[0]);
            Assert.Equal((ushort)3, multi[2]);

            // FC15 — write multiple coils via a hand-built frame (client packing
            // is FluentModbus's business; here we pin OUR codec's bit order)
            byte[] fc15 =
            [
                0x00, 0x05, 0x00, 0x00, 0x00, 0x08, 0x01, // tid=5, proto=0, len=8, unit=1
                0x0F, 0x00, 0x1E, 0x00, 0x04, 0x01, 0x0B, // FC15: addr=30 qty=4 bc=1 bits=1011
            ];
            await SendFrameAsync(port, fc15);
            Assert.True(device.ReadBit(TableKind.Coil, 30));
            Assert.True(device.ReadBit(TableKind.Coil, 31));
            Assert.False(device.ReadBit(TableKind.Coil, 32));
            Assert.True(device.ReadBit(TableKind.Coil, 33));
        }
        finally
        {
            await cts.CancelAsync();
            cts.Token.WaitHandle.WaitOne(3000);
        }
    }

    [Fact]
    public async Task ExceptionResponses_SurfaceAsModbusException()
    {
        var device = new Device("t", holdingRegisters: 10);
        var (port, cts) = await StartServerAsync(device);
        try
        {
            using var client = new ModbusTcpClient();
            client.Connect(new IPEndPoint(IPAddress.Loopback, port), ModbusEndianness.BigEndian);

            // qty 126 > 125 → exception 3 (IllegalDataValue)
            await Assert.ThrowsAnyAsync<ModbusException>(async () =>
                await client.ReadHoldingRegistersAsync<ushort>(1, 0, 126));

            // beyond range → exception 2 (IllegalDataAddress)
            await Assert.ThrowsAnyAsync<ModbusException>(async () =>
                await client.ReadHoldingRegistersAsync<ushort>(1, 100, 1));

            // qty 0 → exception 3
            await Assert.ThrowsAnyAsync<ModbusException>(async () =>
                await client.ReadHoldingRegistersAsync<ushort>(1, 0, 0));
        }
        finally
        {
            await cts.CancelAsync();
            cts.Token.WaitHandle.WaitOne(3000);
        }
    }

    [Fact]
    public async Task CommLog_RecordsStructuredRequests()
    {
        var provider = new CapturingProvider();
        var device = new Device("t", holdingRegisters: 100);
        var (port, cts) = await StartServerAsync(device, provider: provider);
        try
        {
            using var client = new ModbusTcpClient();
            client.Connect(new IPEndPoint(IPAddress.Loopback, port), ModbusEndianness.BigEndian);
            client.WriteSingleRegister(3, 5, 42); // unit 3, addr 5
            await Task.Delay(100);

            var request = provider.Entries.FirstOrDefault(e => e.EventId == 1001);
            Assert.True(request is not null,
                $"entries: {string.Join(" ||| ", provider.Entries.Select(e => $"{e.Level}|{e.EventId}|[{e.Message}]"))}");
            Assert.Equal(LogLevel.Debug, request.Level);
            // conn id is process-global; assert the stable part
            Assert.Matches("^conn=c\\d+ unit=3 fc=06 addr=5 qty=1 result=ok$", request.Message);
        }
        finally
        {
            await cts.CancelAsync();
            cts.Token.WaitHandle.WaitOne(3000);
        }
    }

    [Fact]
    public async Task DisabledUnitId_GetsNoAnswer_ButServerRecovers()
    {
        var device = new Device("t", holdingRegisters: 100);
        var (port, cts) = await StartServerAsync(device, unitIds: [1]);
        try
        {
            using var raw = new System.Net.Sockets.TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, port);
            var stream = raw.GetStream();

            // unit id 2, FC03 addr0 qty1 — not served: expect silence
            byte[] silence = [0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x02, 0x03, 0x00, 0x00, 0x00, 0x01];
            await stream.WriteAsync(silence);
            using var timeout = new CancellationTokenSource(700);
            var buf = new byte[64];
            try
            {
                var answeredTooSoon = await stream.ReadAsync(buf, timeout.Token);
                Assert.Fail($"disabled unit id received an answer of {answeredTooSoon} bytes");
            }
            catch (OperationCanceledException)
            {
                // expected: no answer within the timeout
            }

            // now unit id 1 on the same connection — must work
            byte[] served = [0x00, 0x02, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01];
            await stream.WriteAsync(served);
            var buf2 = new byte[64];
            using var generous = new CancellationTokenSource(5000);
            var n = await stream.ReadAsync(buf2, generous.Token);
            Assert.True(n >= 11, $"expected a response frame, got {n} bytes");
            Assert.Equal(0x03, buf2[7]);
        }
        finally
        {
            await cts.CancelAsync();
            cts.Token.WaitHandle.WaitOne(3000);
        }
    }

    [Fact]
    public async Task MalformedHeader_ClosesConnection()
    {
        var device = new Device("t", holdingRegisters: 100);
        var (port, cts) = await StartServerAsync(device);
        try
        {
            using var raw = new System.Net.Sockets.TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, port);
            var stream = raw.GetStream();

            // wrong protocol id (byte 3 = 1)
            byte[] frame = [0x00, 0x01, 0x00, 0x01, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01];
            await stream.WriteAsync(frame);

            // server should close without answering: the follow-up read either
            // returns 0 (clean FIN) or fails with ConnectionReset (Windows RST)
            var buf = new byte[64];
            using var generous = new CancellationTokenSource(5000);
            var closed = false;
            try
            {
                var n = await stream.ReadAsync(buf, generous.Token);
                closed = n == 0;
            }
            catch (IOException)
            {
                closed = true;
            }
            Assert.True(closed, "connection was not closed after a malformed header");
        }
        finally
        {
            await cts.CancelAsync();
            cts.Token.WaitHandle.WaitOne(3000);
        }
    }

    [Fact]
    public async Task TwoServers_SameDevice_ConcurrentClients()
    {
        var device = new Device("t", holdingRegisters: 100);
        var (port1, cts1) = await StartServerAsync(device);
        var (port2, cts2) = await StartServerAsync(device);
        try
        {
            using var c1 = new ModbusTcpClient();
            using var c2 = new ModbusTcpClient();
            c1.Connect(new IPEndPoint(IPAddress.Loopback, port1), ModbusEndianness.BigEndian);
            c2.Connect(new IPEndPoint(IPAddress.Loopback, port2), ModbusEndianness.BigEndian);
            c1.WriteSingleRegister(1, 0, 11);
            c2.WriteSingleRegister(1, 1, 22);
            Assert.Equal((ushort)11, c1.ReadHoldingRegisters<ushort>(1, 0, 1)[0]);
            Assert.Equal((ushort)22, c2.ReadHoldingRegisters<ushort>(1, 1, 1)[0]);
        }
        finally
        {
            await cts1.CancelAsync();
            await cts2.CancelAsync();
            cts1.Token.WaitHandle.WaitOne(3000);
            cts2.Token.WaitHandle.WaitOne(3000);
        }
    }
}