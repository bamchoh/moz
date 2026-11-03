using Microsoft.Extensions.Logging;
using Moz.Client;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Modbus.Client;

/// <summary>One poll line: read [remote_offset, +count) with fc, write into local table at local_offset.</summary>
public readonly record struct PollEntry(int Fc, int RemoteOffset, int Count, TableKind LocalTable, int LocalOffset);

/// <summary>One push line: write local [local_offset, +count) to the remote at remote_offset with fc (5/6/15/16).</summary>
public readonly record struct PushEntry(int Fc, TableKind LocalTable, int LocalOffset, int Count, int RemoteOffset);

/// <summary>
/// A Modbus slave as seen by the generic client loop: each poll item is one
/// read request (FC01-04), each push item one write request (FC05/06/15/16),
/// exchanged over an <see cref="IMasterLink"/> (TCP or RTU). An exception
/// response is logged (`exc=`) and treated as a rejection: a poll mirrors
/// nothing, a push is not retried. Everything else that goes wrong is an
/// IOException for the loop to reset and retry.
/// </summary>
public sealed class ModbusRemote : IRemoteDevice
{
    private readonly string _name;
    private readonly IMasterLink _link;
    private readonly byte _unit;
    private readonly PollEntry[] _polls;
    private readonly PushEntry[] _pushes;
    private readonly ILogger _logger;

    public ModbusRemote(string name, IMasterLink link, byte unit, PollEntry[] polls, PushEntry[] pushes, ILogger logger)
    {
        _name = name;
        _link = link;
        _unit = unit;
        _polls = polls;
        _pushes = pushes;
        _logger = logger;
        Polls = polls.Select(p => new LocalRange(p.LocalTable, p.LocalOffset, p.Count)).ToArray();
        Pushes = pushes.Select(p => new LocalRange(p.LocalTable, p.LocalOffset, p.Count)).ToArray();
    }

    public string Remote => _link.Remote;
    public bool IsOpen => _link.IsOpen;
    public bool IsConnected => _link.IsConnected;
    public IReadOnlyList<LocalRange> Polls { get; }
    public IReadOnlyList<LocalRange> Pushes { get; }

    public Task ConnectAsync(CancellationToken ct) => _link.ConnectAsync(ct);
    public Task ResetAsync(CancellationToken ct) => _link.ResetAsync(ct);
    public void Close() => _link.Close();

    public async Task<bool[]?> ReadBitsAsync(int poll, CancellationToken ct)
    {
        var entry = _polls[poll];
        var pdu = await ReadAsync(entry, ct);
        return pdu is null ? null : ModbusClient.ParseBitResponse(entry.Fc, entry.Count, pdu);
    }

    public async Task<ushort[]?> ReadWordsAsync(int poll, CancellationToken ct)
    {
        var entry = _polls[poll];
        var pdu = await ReadAsync(entry, ct);
        return pdu is null ? null : ModbusClient.ParseWordResponse(entry.Fc, entry.Count, pdu);
    }

    public Task WriteBitsAsync(int push, bool[] values, CancellationToken ct)
    {
        var entry = _pushes[push];
        return WriteAsync(entry, ModbusClient.BuildWriteRequest(entry.Fc, entry.RemoteOffset, values), ct);
    }

    public Task WriteWordsAsync(int push, ushort[] values, CancellationToken ct)
    {
        var entry = _pushes[push];
        return WriteAsync(entry, ModbusClient.BuildWriteRequest(entry.Fc, entry.RemoteOffset, values), ct);
    }

    /// <summary>One read exchange; the response PDU, or null for an exception response (logged).</summary>
    private async Task<byte[]?> ReadAsync(PollEntry poll, CancellationToken ct)
    {
        var req = ModbusClient.BuildReadRequest(poll.Fc, poll.RemoteOffset, poll.Count);
        var pdu = await _link.ExchangeAsync(_unit, req, ct);
        bool rejected = IsException(pdu);
        _logger.Request(_name, _unit, (byte)poll.Fc,
            RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
            rejected ? ExceptionCodeOf(pdu) : null);
        return rejected ? null : pdu;
    }

    /// <summary>One write exchange; the echo is checked unless the remote answered with an exception (logged).</summary>
    private async Task WriteAsync(PushEntry push, byte[] req, CancellationToken ct)
    {
        var pdu = await _link.ExchangeAsync(_unit, req, ct);
        bool rejected = IsException(pdu);
        if (!rejected)
        {
            ModbusClient.CheckWriteResponse(push.Fc, push.RemoteOffset, push.Count, pdu);
        }
        _logger.Request(_name, _unit, (byte)push.Fc,
            RequestInfo.AddressOf(req), RequestInfo.QuantityOf(req),
            rejected ? ExceptionCodeOf(pdu) : null);
    }

    private static bool IsException(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 1 && (pdu[0] & 0x80) != 0;

    private static byte ExceptionCodeOf(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 2 ? pdu[1] : (byte)0;
}
