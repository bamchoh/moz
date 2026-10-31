// Smoke test against a live moz process (started separately from the CLI).
// Runs only when MOZ_SMOKE_PORT points at a running server; otherwise passes trivially.
using ModbusTcpClient = FluentModbus.ModbusTcpClient;
using ModbusEndianness = FluentModbus.ModbusEndianness;

namespace Moz.Tests;

public class ToolSmokeTests
{
    [Fact]
    public async Task LiveCliRoundTrip()
    {
        var p = Environment.GetEnvironmentVariable("MOZ_SMOKE_PORT");
        if (p is null || !ushort.TryParse(p, out var port))
        {
            return; // gated: set MOZ_SMOKE_PORT=<port> to run against a live moz instance
        }

        using var client = new ModbusTcpClient();
        client.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), ModbusEndianness.BigEndian);

        client.WriteSingleRegister(1, 0, 0);
        Assert.Equal((ushort)0, client.ReadHoldingRegisters<ushort>(1, 0, 5)[0]);
        client.WriteSingleRegister(1, 0, 1234);
        Assert.Equal((ushort)1234, client.ReadHoldingRegisters<ushort>(1, 0, 1)[0]);
        await Task.CompletedTask;
    }
}