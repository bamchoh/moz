using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Modbus.Tcp;

/// <summary>
/// One accepted TCP connection: read MBAP frames, dispatch PDUs, write
/// responses until EOF, error, or server shutdown. Owns the TcpClient.
/// Buffer discipline: the codec writes the response PDU into
/// <c>_respPdu</c>; the frame buffer gets a header + a copy of that PDU,
/// so neither layer's buffers overlap.
/// </summary>
internal sealed class TcpConnection(
    TcpClient client,
    IDeviceAccess device,
    ushort[]? unitIds,
    ILogger logger,
    string connId,
    string remote) : IDisposable
{
    private const int RespPduSize = ModbusLimits.PduMaxLength + 2; // codec output, incl. exceptions

    private readonly NetworkStream _stream = client.GetStream();
    private readonly byte[] _header = new byte[Mbap.HeaderSize];
    private readonly byte[] _reqPdu = new byte[ModbusLimits.PduMaxLength];
    private readonly byte[] _respPdu = new byte[RespPduSize];
    private readonly byte[] _respFrame = new byte[Mbap.HeaderSize + RespPduSize];

    public async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _stream.ReadExactlyAsync(_header, ct);
            }
            catch (EndOfStreamException)
            {
                return; // graceful or partial peer close: nothing left to answer
            }

            if (!Mbap.TryParseHeader(_header, out var mbap))
            {
                logger.MalformedFrame(connId, remote, "bad MBAP header (protocol id / length field)");
                return; // spec-modbus.md: close, do not answer lies with more bytes
            }

            int pduLength = mbap.Length - 1; // length field counts unit id + PDU
            try
            {
                await _stream.ReadExactlyAsync(_reqPdu.AsMemory(0, pduLength), ct);
            }
            catch (EndOfStreamException)
            {
                logger.MalformedFrame(connId, remote, "truncated frame (peer closed mid-frame)");
                return;
            }

            if (!UnitIdAllowed(mbap.UnitId))
            {
                // karakuri-style: disabled unit ids get no answer (master times out)
                logger.MalformedFrame(connId, remote, $"unit id {mbap.UnitId} is not served");
                continue;
            }

            PduOutcome outcome;
            try
            {
                outcome = ModbusPdu.Handle(_reqPdu.AsSpan(0, pduLength), device, _respPdu);
            }
            catch (Exception ex)
            {
                // the "chute": an unexpected device failure maps to exception 04, never escalation
                logger.LogWarning(ex, "device failure while handling fc {Fc:X2}", _reqPdu[0]);
                outcome = ModbusPdu.EncodeException(_respPdu, (FunctionCode)_reqPdu[0],
                    ExceptionCode.ServerDeviceFailure);
            }

            Mbap.WriteHeader(_respFrame, mbap.TransactionId, mbap.UnitId, outcome.Length);
            _respPdu.AsSpan(0, outcome.Length).CopyTo(_respFrame.AsSpan(Mbap.HeaderSize));
            await _stream.WriteAsync(_respFrame.AsMemory(0, Mbap.HeaderSize + outcome.Length), ct);

            logger.Request(connId, mbap.UnitId, _reqPdu[0],
                GetRequestAddress(_reqPdu, pduLength), GetRequestQuantity(_reqPdu, pduLength),
                outcome.ExceptionCode == 0 ? null : outcome.ExceptionCode);
        }
    }

    private bool UnitIdAllowed(byte unitId) =>
        unitIds is null || unitIds.Length == 0 || Array.IndexOf(unitIds, unitId) >= 0;

    /// <summary>First address word for logging; 0 when the PDU is too short to have one.</summary>
    private static ushort GetRequestAddress(ReadOnlySpan<byte> pdu, int pduLength) =>
        pduLength >= 3 ? (ushort)((pdu[1] << 8) | pdu[2]) : (ushort)0;

    /// <summary>Quantity for logging; single-write FCs report 1, unknown FCs 0.</summary>
    private static ushort GetRequestQuantity(ReadOnlySpan<byte> pdu, int pduLength)
    {
        switch ((FunctionCode)pdu[0])
        {
            case FunctionCode.ReadCoils:
            case FunctionCode.ReadDiscreteInputs:
            case FunctionCode.ReadHoldingRegisters:
            case FunctionCode.ReadInputRegisters:
            case FunctionCode.WriteMultipleCoils:
            case FunctionCode.WriteMultipleRegisters:
                return pduLength >= 5 ? (ushort)((pdu[3] << 8) | pdu[4]) : (ushort)0;
            case FunctionCode.WriteSingleCoil:
            case FunctionCode.WriteSingleRegister:
                return pduLength >= 5 ? (ushort)1 : (ushort)0;
            default:
                return 0;
        }
    }

    public void Dispose() => client.Dispose();
}