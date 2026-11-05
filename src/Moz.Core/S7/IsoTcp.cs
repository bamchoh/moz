using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.S7;

/// <summary>
/// ISO-on-TCP (RFC 1006) framing used by S7comm: TPKT (03 00 length) and
/// COTP (length | code | ...). A session starts with a connect request
/// (E0) answered by a connect confirm (D0); S7 PDUs travel in data TPDUs
/// (02 F0 80). moz does not fragment and accepts any TSAP.
/// </summary>
public static class IsoTcp
{
    public const int TpktSize = 4;
    public const int FrameMax = 2048;

    public const byte ConnectRequest = 0xE0;
    public const byte ConnectConfirmCode = 0xD0;
    public const byte DataTransfer = 0xF0;

    /// <summary>Version 3 and a total length that can hold a COTP header within the frame limit.</summary>
    public static bool TryParseTpkt(ReadOnlySpan<byte> header, out int length)
    {
        length = 0;
        if (header.Length < TpktSize || header[0] != 3)
        {
            return false;
        }
        length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        return length >= TpktSize + 3 && length <= FrameMax;
    }

    /// <summary>COTP header: length indicator, code; the payload starts after the header.</summary>
    public static bool TryParseCotp(ReadOnlySpan<byte> cotp, out byte code, out int payloadOffset)
    {
        code = 0;
        payloadOffset = 0;
        if (cotp.Length < 2 || 1 + cotp[0] > cotp.Length)
        {
            return false;
        }
        code = cotp[1];
        payloadOffset = 1 + cotp[0];
        return true;
    }

    /// <summary>The connect confirm for a connect request: the client's source reference becomes the destination, our reference is 1, the parameters are echoed.</summary>
    public static byte[] ConnectConfirm(ReadOnlySpan<byte> cr)
    {
        int length = cr[0];
        var cotp = new byte[1 + length];
        cr[..(1 + length)].CopyTo(cotp);
        cotp[1] = ConnectConfirmCode;
        cotp[2] = cr[4];
        cotp[3] = cr[5];
        cotp[4] = 0;
        cotp[5] = 1;
        cotp[6] = 0;
        return Tpkt(cotp);
    }

    /// <summary>A data TPDU (02 F0 80) carrying one S7 PDU.</summary>
    public static byte[] Data(ReadOnlySpan<byte> payload)
    {
        var cotp = new byte[3 + payload.Length];
        cotp[0] = 2;
        cotp[1] = DataTransfer;
        cotp[2] = 0x80;
        payload.CopyTo(cotp.AsSpan(3));
        return Tpkt(cotp);
    }

    private static byte[] Tpkt(byte[] cotp)
    {
        var frame = new byte[TpktSize + cotp.Length];
        frame[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)frame.Length);
        cotp.CopyTo(frame, TpktSize);
        return frame;
    }
}

/// <summary>
/// S7 server over ISO-on-TCP (the real units listen on 102) — one
/// <see cref="IProtocolRunner"/> per [[server]] with protocol "s7". Each
/// connection has its own negotiated PDU length. A frame that is not TPKT
/// / COTP, or a data TPDU that is not an S7 job, closes the connection
/// without an answer; the COTP connect is optional (data before it is
/// served).
/// </summary>
public sealed class S7TcpServer : IProtocolRunner
{
    private readonly IPEndPoint _bind;
    private readonly IDeviceAccess _device;
    private readonly S7Map[] _maps;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public S7TcpServer(string name, IPEndPoint bind, IDeviceAccess device, S7Map[] maps, ILogger logger)
    {
        Name = name;
        _bind = bind;
        _device = device;
        _maps = maps;
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>The concrete endpoint after Start (set by <see cref="RunAsync"/>).</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    /// <summary>Completes once the listener is bound; faults when listening failed.</summary>
    public Task Ready => _ready.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_bind);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _logger.ListenFailed(_bind.ToString(), ex.Message);
            _ready.TrySetException(ex);
            throw;
        }
        BoundEndpoint = (IPEndPoint)listener.LocalEndpoint;
        _ready.TrySetResult();
        _logger.LogInformation("listening {Name} on {Endpoint} (s7)", Name, BoundEndpoint);

        var pending = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.ListenFailed(Name, ex.Message);
                    continue;
                }
                pending.Add(HandleConnectionAsync(client, ct));
            }
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connId = $"c{ConnectionCounter.Increment()}";
        try
        {
            _logger.ConnAccepted(connId, remote);
            using (client)
            {
                await ServeAsync(client.GetStream(), connId, remote, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // server shutting down mid-frame
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            _logger.ConnError(connId, remote, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.ConnError(connId, remote, $"unexpected: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _logger.ConnClosed(connId, remote);
        }
    }

    private async Task ServeAsync(NetworkStream stream, string connId, string remote, CancellationToken ct)
    {
        var session = new S7Session();
        var header = new byte[IsoTcp.TpktSize];
        var frame = new byte[IsoTcp.FrameMax];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await stream.ReadExactlyAsync(header, ct);
            }
            catch (EndOfStreamException)
            {
                return; // peer closed
            }
            if (!IsoTcp.TryParseTpkt(header, out var length))
            {
                _logger.MalformedFrame(connId, remote, $"bad TPKT header: {Convert.ToHexString(header)}");
                return; // close without an answer
            }
            var rest = frame.AsMemory(0, length - IsoTcp.TpktSize);
            try
            {
                await stream.ReadExactlyAsync(rest, ct);
            }
            catch (EndOfStreamException)
            {
                _logger.MalformedFrame(connId, remote, "truncated frame (peer closed mid-frame)");
                return;
            }
            if (!IsoTcp.TryParseCotp(rest.Span, out var code, out var payloadOffset))
            {
                _logger.MalformedFrame(connId, remote, $"bad COTP header: {Convert.ToHexString(rest.Span)}");
                return;
            }
            switch (code)
            {
                case IsoTcp.ConnectRequest:
                    await stream.WriteAsync(IsoTcp.ConnectConfirm(rest.Span), ct);
                    _logger.LogDebug("s7 connect {Conn}: {Cotp}", connId, Convert.ToHexString(rest.Span));
                    break;

                case IsoTcp.DataTransfer:
                    S7Outcome outcome;
                    try
                    {
                        if (!S7Server.TryHandle(rest.Span[payloadOffset..], _device, _maps, session, out outcome))
                        {
                            _logger.MalformedFrame(connId, remote, $"bad S7 PDU: {Convert.ToHexString(rest.Span[payloadOffset..])}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        // the "chute": an unexpected device failure must not kill the connection
                        _logger.LogWarning(ex, "device failure while handling an S7 request");
                        return;
                    }
                    await stream.WriteAsync(IsoTcp.Data(outcome.Response), ct);
                    _logger.Request(connId, outcome.Op, outcome.Target, outcome.Points, outcome.Error);
                    break;

                default:
                    // disconnect request or anything else: the session is over
                    _logger.LogDebug("s7 COTP {Code:X2} on {Conn}: closing", code, connId);
                    return;
            }
        }
    }
}
