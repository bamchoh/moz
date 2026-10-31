using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Modbus.Tcp;

/// <summary>
/// Modbus TCP server (MBAP framing over TcpListener) — one
/// <see cref="IProtocolRunner"/> per config [server]. Concurrent connections
/// share one <see cref="Moz.Devices.Device"/>; the device lock serializes
/// memory access.
/// </summary>
public sealed class ModbusTcpServer : IProtocolRunner
{
    private readonly IPEndPoint _bind;
    private readonly IDeviceAccess _device;
    private readonly ushort[]? _unitIds; // empty/null = accept any unit id (karakuri-style)
    private readonly ILogger _logger;

    public ModbusTcpServer(string name, IPEndPoint bind, IDeviceAccess device,
        ushort[]? unitIds, ILogger logger)
    {
        Name = name;
        _bind = bind;
        _device = device;
        _unitIds = unitIds;
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>The concrete endpoint after Start (set by <see cref="RunAsync"/>).</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    /// <summary>True while <see cref="RunAsync"/> is listening.</summary>
    public bool IsRunning { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(_bind);
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            // port occupied, permission denied, etc. — fatal for this runner
            _logger.ListenFailed(_bind.ToString(), ex.Message);
            throw;
        }
        BoundEndpoint = (IPEndPoint)listener.LocalEndpoint;
        IsRunning = true;
        _logger.LogInformation("listening {Name} on {Endpoint}", Name, BoundEndpoint);

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
                    // transient accept failure: log and keep accepting
                    _logger.ListenFailed(Name, ex.Message);
                    continue;
                }
                pending.Add(HandleConnectionAsync(client, ct));
            }
        }
        finally
        {
            IsRunning = false;
            listener.Stop();
            // wait briefly for in-flight connections to wind down
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var connId = ConnectionCounter.Increment();
        try
        {
            _logger.ConnAccepted($"c{connId}", remote);
            using var connection = new TcpConnection(client, _device, _unitIds, _logger, $"c{connId}", remote);
            await connection.ServeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // server shutting down mid-frame
        }
        catch (IOException ex)
        {
            _logger.ConnError($"c{connId}", remote, ex.Message);
        }
        catch (SocketException ex)
        {
            _logger.ConnError($"c{connId}", remote, ex.Message);
        }
        catch (Exception ex)
        {
            // never let one connection kill the server
            _logger.ConnError($"c{connId}", remote, $"unexpected: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _logger.ConnClosed($"c{connId}", remote);
        }
    }
}

internal static class ConnectionCounter
{
    private static int _next;
    public static int Increment() => Interlocked.Increment(ref _next);
}