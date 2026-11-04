using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Fins;

/// <summary>
/// FINS/TCP framing: "FINS" | length (4, big-endian; bytes after this
/// field) | command (4) | error code (4) | payload. A session starts with a
/// node address exchange (command 0 → 1), then carries FINS frames
/// (command 2 both ways); command 3 reports a framing error.
/// </summary>
public static class FinsTcp
{
    public const int HeaderSize = 16;

    public const uint CommandNodeAddressSend = 0;
    public const uint CommandNodeAddressConfirm = 1;
    public const uint CommandFrame = 2;
    public const uint CommandError = 3;

    public const uint ErrorHeader = 1;
    public const uint ErrorLength = 2;
    public const uint ErrorUnsupportedCommand = 3;

    private static ReadOnlySpan<byte> Magic => "FINS"u8;

    /// <summary>Magic and a length that can hold the command and error code fields within the frame limit.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> header, out uint command, out uint errorCode, out int payloadLength)
    {
        command = 0;
        errorCode = 0;
        payloadLength = 0;
        if (header.Length < HeaderSize || !header[..4].SequenceEqual(Magic))
        {
            return false;
        }
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        if (length < 8 || length > 8 + Fins.FrameMax)
        {
            return false;
        }
        command = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        errorCode = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        payloadLength = (int)length - 8;
        return true;
    }

    public static byte[] Build(uint command, uint errorCode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        Magic.CopyTo(frame);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)(8 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(12), errorCode);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>The answer to a node address send: client node (4) + server node (4).</summary>
    public static byte[] NodeAddressConfirm(byte clientNode, byte serverNode) =>
        Build(CommandNodeAddressConfirm, 0, [0, 0, 0, clientNode, 0, 0, 0, serverNode]);

    /// <summary>
    /// The client's node: as requested when non-zero; otherwise the last octet
    /// of its address like a real CPU unit, falling back to the first free
    /// number when that octet is 0, 255 or the server's own node.
    /// </summary>
    public static byte AssignClientNode(byte requested, IPAddress remote, byte serverNode)
    {
        if (requested != 0)
        {
            return requested;
        }
        byte candidate = remote.GetAddressBytes()[^1];
        if (candidate is 0 or 255 || candidate == serverNode)
        {
            for (int n = 1; n <= 254; n++)
            {
                if (n != serverNode)
                {
                    return (byte)n;
                }
            }
        }
        return candidate;
    }

    /// <summary>The server's own node when the config has none: the bind address's last octet, or 1 when that is not a node number.</summary>
    public static byte DefaultNode(IPAddress bind)
    {
        byte last = bind.GetAddressBytes()[^1];
        return last is 0 or 255 ? (byte)1 : last;
    }
}

/// <summary>
/// FINS/TCP server — one <see cref="IProtocolRunner"/> per [[server]] with
/// protocol "fins-tcp". The simulated CPU's areas live in the shared device
/// memory through <see cref="FinsMap"/>s. A frame with a bad magic or an
/// impossible length closes the connection without an answer; an unknown
/// FINS/TCP command is answered with an error frame and then closed; the
/// node address exchange is optional (frames before it are served).
/// </summary>
public sealed class FinsTcpServer : IProtocolRunner
{
    private readonly IPEndPoint _bind;
    private readonly byte _node;
    private readonly IDeviceAccess _device;
    private readonly FinsMap[] _maps;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FinsTcpServer(string name, IPEndPoint bind, byte node, IDeviceAccess device, FinsMap[] maps, ILogger logger)
    {
        Name = name;
        _bind = bind;
        _node = node;
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
        _logger.LogInformation("listening {Name} on {Endpoint} (fins-tcp, node {Node})", Name, BoundEndpoint, _node);

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
        var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.Loopback;
        var connId = $"c{ConnectionCounter.Increment()}";
        try
        {
            _logger.ConnAccepted(connId, remote);
            using (client)
            {
                await ServeAsync(client.GetStream(), connId, remote, remoteAddress, ct);
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

    private async Task ServeAsync(NetworkStream stream, string connId, string remote, IPAddress remoteAddress, CancellationToken ct)
    {
        var header = new byte[FinsTcp.HeaderSize];
        var payload = new byte[Fins.FrameMax];
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
            if (!FinsTcp.TryParseHeader(header, out var command, out _, out var length))
            {
                _logger.MalformedFrame(connId, remote, $"bad FINS/TCP header: {Convert.ToHexString(header)}");
                return; // close without an answer
            }
            try
            {
                await stream.ReadExactlyAsync(payload.AsMemory(0, length), ct);
            }
            catch (EndOfStreamException)
            {
                _logger.MalformedFrame(connId, remote, "truncated frame (peer closed mid-frame)");
                return;
            }
            var body = payload.AsMemory(0, length);

            switch (command)
            {
                case FinsTcp.CommandNodeAddressSend:
                    if (length < 4)
                    {
                        _logger.MalformedFrame(connId, remote, "node address send without a node");
                        return;
                    }
                    var clientNode = FinsTcp.AssignClientNode(body.Span[3], remoteAddress, _node);
                    await stream.WriteAsync(FinsTcp.NodeAddressConfirm(clientNode, _node), ct);
                    _logger.LogDebug("fins node exchange {Conn}: client {Client} server {Server}", connId, clientNode, _node);
                    break;

                case FinsTcp.CommandFrame:
                    FinsOutcome outcome;
                    try
                    {
                        if (!FinsServer.TryHandle(body.Span, _device, _maps, out outcome))
                        {
                            _logger.MalformedFrame(connId, remote, $"bad FINS frame: {Convert.ToHexString(body.Span)}");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        // the "chute": an unexpected device failure must not kill the connection
                        _logger.LogWarning(ex, "device failure while handling a FINS request");
                        return;
                    }
                    if (outcome.ResponseRequired)
                    {
                        await stream.WriteAsync(FinsTcp.Build(FinsTcp.CommandFrame, 0, outcome.Response), ct);
                    }
                    _logger.Request(connId, outcome.Op, outcome.Target, outcome.Points,
                        outcome.EndCode == Fins.EndOk ? null : $"{outcome.EndCode:X4}");
                    break;

                default:
                    _logger.MalformedFrame(connId, remote, $"unsupported FINS/TCP command {command}");
                    await stream.WriteAsync(FinsTcp.Build(FinsTcp.CommandError, FinsTcp.ErrorUnsupportedCommand, []), ct);
                    return;
            }
        }
    }
}
