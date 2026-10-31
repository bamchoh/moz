using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Modbus.Rtu;

/// <summary>
/// Modbus RTU server over an arbitrary byte stream (usually a serial port
/// opened by the CLI; tests use in-memory pipes). Framing: accumulate bytes
/// and dispatch a request as soon as the FC-derived length + CRC parse
/// cleanly; the inter-frame idle gap (RTU's real frame delimiter) discards
/// partial buffers. Bad-CRC frames and unserved slave addresses get no
/// answer (the master sees its own timeout) — the loop keeps going.
/// </summary>
public sealed class RtuServer : IProtocolRunner
{
    private const int ByteEof = -2;
    private const int ByteIdle = -1;

    private readonly Stream _link;
    private readonly IDeviceAccess _device;
    private readonly ushort[]? _unitIds;
    private readonly int _idleGapMs;
    private readonly ILogger _logger;

    public RtuServer(string name, Stream link, IDeviceAccess device,
        ushort[]? unitIds, int idleGapMs, ILogger logger)
    {
        Name = name;
        _link = link;
        _device = device;
        _unitIds = unitIds;
        _idleGapMs = idleGapMs;
        _logger = logger;
    }

    public string Name { get; }

    public async Task RunAsync(CancellationToken ct)
    {
        // A closed/given-up link or an unreadable device ends this runner;
        // frame-level garbage is handled by policy below, never by exception.
        var buf = new byte[Rtu.AduMaxSize];
        try
        {
            await AwaitRequestsAsync(buf, ct);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (IOException ex)
        {
            _logger.ConnError(Name, "-", ex.Message);
        }
    }

    /// <summary>Accumulate one request frame and dispatch it; false when the link ends (EOF).</summary>
    private async Task AwaitRequestsAsync(byte[] buf, CancellationToken ct)
    {
        var length = 0;
        while (!ct.IsCancellationRequested)
        {
            var b = await ReadByteAsync(length == 0 ? null : _idleGapMs, ct);
            switch (b)
            {
                case ByteEof:
                    if (length > 0)
                    {
                        _logger.MalformedFrame(Name, "-", $"truncated frame discarded ({length}B)");
                    }
                    return; // link closed

                case ByteIdle:
                    // RTU's real frame delimiter: whatever accumulated is not a complete request
                    if (length > 0)
                    {
                        _logger.MalformedFrame(Name, "-",
                            $"idle gap discarded {length} byte(s): {Rtu.HexDump(buf.AsSpan(0, length))}");
                        length = 0;
                    }
                    continue;

                default:
                    buf[length++] = (byte)b;
                    break;
            }

            if (length > Rtu.AduMaxSize)
            {
                _logger.MalformedFrame(Name, "-", $"frame over {Rtu.AduMaxSize} bytes discarded");
                length = 0;
                continue;
            }

            if (Rtu.TryParseFrame(buf.AsSpan(0, length), out var addr, out var pdu))
            {
                Dispatch(addr, pdu);
                length = 0; // ready for the next request
            }
        }
    }

    private void Dispatch(byte address, ReadOnlySpan<byte> pdu)
    {
        if (!UnitIdAllowed(address))
        {
            _logger.MalformedFrame(Name, "-", $"RTU slave address {address} is not served");
            return; // silence — master timeout, karakuri parity
        }

        Span<byte> respPdu = stackalloc byte[ModbusLimits.PduMaxLength + 2];
        PduOutcome outcome;
        try
        {
            outcome = ModbusPdu.Handle(pdu, _device, respPdu);
        }
        catch (Exception ex)
        {
            // the "chute": unexpected device failure maps to exception 04
            _logger.LogWarning(ex, "device failure while handling fc {Fc:X2}", pdu[0]);
            outcome = ModbusPdu.EncodeException(respPdu, (FunctionCode)pdu[0],
                ExceptionCode.ServerDeviceFailure);
        }

        var adu = Rtu.Frame(address, respPdu[..outcome.Length]);
        _link.Write(adu);

        _logger.Request(Name, address, pdu[0],
            RequestInfo.AddressOf(pdu), RequestInfo.QuantityOf(pdu),
            outcome.ExceptionCode == 0 ? null : outcome.ExceptionCode);
    }

    private bool UnitIdAllowed(byte unitId) =>
        _unitIds is null || _unitIds.Length == 0 || Array.IndexOf(_unitIds, unitId) >= 0;

    /// <summary>
    /// Read one byte through the idle window: RTU frames are delimited by a
    /// silent interval (3.5 characters ≈ 4ms at 9600 baud). Returns a byte,
    /// <see cref="ByteEof"/> on link end, or <see cref="ByteIdle"/> on timeout.
    /// </summary>
    private async Task<int> ReadByteAsync(int? timeoutMs, CancellationToken ct)
    {
        var b = new byte[1];
        try
        {
            int n;
            if (timeoutMs is { } ms)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ms);
                n = await _link.ReadAsync(b, cts.Token);
            }
            else
            {
                n = await _link.ReadAsync(b, ct);
            }
            return n == 0 ? ByteEof : b[0];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ByteIdle;
        }
    }
}