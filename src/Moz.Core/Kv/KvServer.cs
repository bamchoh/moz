using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Kv;

/// <summary>
/// Keyence KV host link server over TCP (the real units listen on 8501) —
/// one <see cref="IProtocolRunner"/> per [[server]] with protocol
/// "kv-hostlink". Requests are ASCII lines ended by CR (CR LF and LF are
/// accepted too); every response ends with CR LF. A line longer than
/// <see cref="KvHostLink.LineMax"/> closes the connection without an answer.
/// </summary>
public sealed class KvServer : IProtocolRunner
{
    private readonly IPEndPoint _bind;
    private readonly IDeviceAccess _device;
    private readonly KvMap[] _maps;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public KvServer(string name, IPEndPoint bind, IDeviceAccess device, KvMap[] maps, ILogger logger)
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
        _logger.LogInformation("listening {Name} on {Endpoint} (kv-hostlink)", Name, BoundEndpoint);

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
            // server shutting down mid-line
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
        var buffer = new byte[1024];
        var line = new byte[KvHostLink.LineMax];
        int length = 0;
        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0)
            {
                return; // peer closed
            }
            for (int i = 0; i < n; i++)
            {
                byte b = buffer[i];
                if (b is (byte)'\r' or (byte)'\n')
                {
                    if (length == 0)
                    {
                        continue; // the LF of a CR LF, or an empty line: nothing to answer
                    }
                    var text = Encoding.ASCII.GetString(line, 0, length);
                    length = 0;
                    KvOutcome outcome;
                    try
                    {
                        outcome = KvHostLink.Handle(text, _device, _maps);
                    }
                    catch (Exception ex)
                    {
                        // the "chute": an unexpected device failure must not kill the connection
                        _logger.LogWarning(ex, "device failure while handling a KV host link request");
                        return;
                    }
                    if (outcome.Response is { } response)
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\r\n"), ct);
                    }
                    _logger.Request(connId, outcome.Op, outcome.Target, outcome.Points, outcome.Error);
                    continue;
                }
                if (length == line.Length)
                {
                    _logger.MalformedFrame(connId, remote, $"request line longer than {KvHostLink.LineMax} bytes");
                    return; // close without an answer
                }
                line[length++] = b;
            }
        }
    }
}
