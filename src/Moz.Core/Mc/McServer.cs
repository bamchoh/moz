using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Mc;

/// <summary>
/// MC protocol server (3E frame, binary code, over TcpListener) — one
/// <see cref="IProtocolRunner"/> per [[server]] with protocol "mc-3e". The
/// simulated CPU's devices live in the shared device memory through
/// <see cref="McMap"/>s. Concurrent connections share the device; a
/// malformed frame (wrong subheader, impossible length) closes the
/// connection without an answer, every well-formed request gets a response
/// (end code on error).
/// </summary>
public sealed class McServer : IProtocolRunner
{
    private readonly IPEndPoint _bind;
    private readonly IDeviceAccess _device;
    private readonly McMap[] _maps;
    private readonly ILogger _logger;

    public McServer(string name, IPEndPoint bind, IDeviceAccess device, McMap[] maps, ILogger logger)
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
            throw;
        }
        BoundEndpoint = (IPEndPoint)listener.LocalEndpoint;
        _logger.LogInformation("listening {Name} on {Endpoint} (mc-3e)", Name, BoundEndpoint);

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
        var header = new byte[Mc3E.HeaderSize];
        var body = new byte[ushort.MaxValue];
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
            if (!Mc3EServer.TryParseRequestHeader(header, out var length))
            {
                _logger.MalformedFrame(connId, remote, $"bad MC request header: {Convert.ToHexString(header)}");
                return; // close without an answer, like a bad MBAP header
            }
            try
            {
                await stream.ReadExactlyAsync(body.AsMemory(0, length), ct);
            }
            catch (EndOfStreamException)
            {
                _logger.MalformedFrame(connId, remote, "truncated frame (peer closed mid-frame)");
                return;
            }

            McOutcome outcome;
            try
            {
                outcome = Mc3EServer.Handle(header, body.AsSpan(0, length), _device, _maps);
            }
            catch (Exception ex)
            {
                // the "chute": an unexpected device failure must not kill the connection
                _logger.LogWarning(ex, "device failure while handling an MC request");
                return;
            }
            await stream.WriteAsync(outcome.Response, ct);
            _logger.Request(connId, outcome.Op, outcome.Target, outcome.Points,
                outcome.EndCode == Mc3EServer.EndOk ? null : $"{outcome.EndCode:X4}");
        }
    }
}

internal static class ConnectionCounter
{
    private static int _next;
    public static int Increment() => Interlocked.Increment(ref _next);
}
