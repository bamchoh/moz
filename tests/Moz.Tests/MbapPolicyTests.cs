using System.Net.Sockets;

namespace Moz.Tests;

/// <summary>
/// Pins the MBAP connection-level policy of docs/spec-modbus.md:
/// wrong protocol id / lying length field → close without answering;
/// truncated frames → MalformedFrame logged + close; request framing
/// quirks (1-byte PDU, unknown FC) → normal exception responses.
/// </summary>
public class MbapPolicyTests
{
    private static CancellationToken TimeoutCts(int ms)
    {
        var cts = new CancellationTokenSource(ms);
        return cts.Token;
    }

    private static async Task<byte[]> ReceiveAsync(RawClient raw) => await raw.ReceiveAsync(TimeoutCts(5000));

    [Fact]
    public async Task TransactionId_AndUnitId_AreEchoed()
    {
        await using var server = await TestServer.Start();
        using var raw = new RawClient(server);
        await raw.ConnectAsync();

        // tid=0xBEEF, unit=7, FC03 addr=0 qty=1
        await raw.WriteAsync([0xBE, 0xEF, 0x00, 0x00, 0x00, 0x06, 0x07, 0x03, 0x00, 0x00, 0x00, 0x01],
            TimeoutCts(5000));
        var resp = await ReceiveAsync(raw);

        Assert.True(resp.Length >= 11, $"got {resp.Length} bytes");
        Assert.Equal(0xBE, resp[0]);
        Assert.Equal(0xEF, resp[1]);
        Assert.Equal(7, resp[6]);
        Assert.Equal(0x03, resp[7]);
        Assert.Equal(2, resp[8]); // byte count for 1 register
    }

    [Fact]
    public async Task UnknownFunctionCode_ThroughTcp_ExceptionResponse01()
    {
        await using var server = await TestServer.Start();
        using var raw = new RawClient(server);
        await raw.ConnectAsync();

        // FC 0x99 with 1 byte of data → exception 0x99 0x01
        await raw.WriteAsync([0x00, 0x03, 0x00, 0x00, 0x00, 0x03, 0x01, 0x99, 0x00], TimeoutCts(5000));
        var resp = await ReceiveAsync(raw);

        Assert.True(resp.Length >= 9, $"got {resp.Length} bytes");
        Assert.Equal(0x99, resp[7]);
        Assert.Equal(0x01, resp[8]);
    }

    [Fact]
    public async Task PduShorterThanFcMinimum_Exception03NotClose()
    {
        await using var server = await TestServer.Start();
        using var raw = new RawClient(server);
        await raw.ConnectAsync();

        // len=2 → PDU is just the function code byte (FC03 without arguments)
        await raw.WriteAsync([0x00, 0x04, 0x00, 0x00, 0x00, 0x02, 0x01, 0x03], TimeoutCts(5000));
        var resp = await ReceiveAsync(raw);

        Assert.True(resp.Length >= 9, $"got {resp.Length} bytes"); // answered, not closed
        Assert.Equal(0x83, resp[7]);
        Assert.Equal(0x03, resp[8]);
    }

    [Fact]
    public async Task OversizedLengthField_ClosesConnection_NoAnswer()
    {
        var provider = new CapturingProvider();
        await using var server = await TestServer.Start(provider: provider);
        using var raw = new RawClient(server);
        await raw.ConnectAsync();
        var before = provider.Entries.Count;

        await raw.WriteAsync([0x00, 0x05, 0x00, 0x00, 0xFF, 0xFF, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01],
            TimeoutCts(5000));

        var resp = await ReadUntilClosedAsync(raw);
        Assert.True(resp.Length == 0, $"answered despite a lying length field: {resp.Length} bytes");
        Assert.Contains(provider.Entries.Skip(before), e => e.EventId == 2004); // MalformedFrame
    }

    [Fact]
    public async Task TinyLengthField_ClosesConnection_NoAnswer()
    {
        var provider = new CapturingProvider();
        await using var server = await TestServer.Start(provider: provider);
        using var raw = new RawClient(server);
        await raw.ConnectAsync();
        var before = provider.Entries.Count;

        await raw.WriteAsync([0x00, 0x06, 0x00, 0x00, 0x00, 0x01, 0x01], TimeoutCts(5000)); // len=1 < 2

        var resp = await ReadUntilClosedAsync(raw);
        Assert.True(resp.Length == 0, $"answered despite len=1: {resp.Length} bytes");
        Assert.Contains(provider.Entries.Skip(before), e => e.EventId == 2004);
    }

    [Fact]
    public async Task TruncatedPdu_LogsMalformed_ThenCloses()
    {
        var provider = new CapturingProvider();
        await using var server = await TestServer.Start(provider: provider);
        var before = provider.Entries.Count;

        // header says 5 PDU bytes; only 3 arrive, then the peer closes mid-frame.
        // The policy: log MalformedFrame, close, keep accepting new connections.
        using (var raw = new RawClient(server))
        {
            await raw.ConnectAsync();
            await raw.WriteAsync([0x00, 0x07, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00],
                TimeoutCts(5000));
        } // FIN

        await WaitFor(server.Provider!, 2004, TimeSpan.FromSeconds(5)); // MalformedFrame logged
        Assert.Contains(provider.Entries, e => e.EventId == 2004);
    }

    private static async Task WaitFor(CapturingProvider provider, int eventId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (provider.Entries.Any(e => e.EventId == eventId))
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.Fail($"event {eventId} not logged within {within}; entries: {string.Join(" | ", provider.Entries.Select(e => $"{e.Level}|{e.EventId}|[{e.Message}]"))}");
    }

    /// <summary>
    /// Read until the server closes or errors. Empty array = clean close or
    /// no data (Windows may raise ConnectionReset instead of returning 0,
    /// which the catch below treats the same way).
    /// </summary>
    private static async Task<byte[]> ReadUntilClosedAsync(RawClient raw)
    {
        try
        {
            return await raw.ReceiveAsync(TimeoutCts(5000));
        }
        catch (OperationCanceledException)
        {
            return []; // 5s of silence counts as "no answer"
        }
        catch (IOException)
        {
            return [];
        }
        catch (SocketException)
        {
            return [];
        }
    }
}