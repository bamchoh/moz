using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Fins;

/// <summary>
/// FINS/UDP server — one <see cref="IProtocolRunner"/> per [[server]] with
/// protocol "fins-udp". One datagram = one FINS frame (no framing, no node
/// exchange); the response goes back to the sender unless ICF says none is
/// required. A datagram that is not a command frame is dropped (logged as
/// malformed).
/// </summary>
public sealed class FinsUdpServer : IProtocolRunner
{
    private readonly IPEndPoint _bind;
    private readonly IDeviceAccess _device;
    private readonly FinsMap[] _maps;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FinsUdpServer(string name, IPEndPoint bind, IDeviceAccess device, FinsMap[] maps, ILogger logger)
    {
        Name = name;
        _bind = bind;
        _device = device;
        _maps = maps;
        _logger = logger;
    }

    public string Name { get; }

    /// <summary>The concrete endpoint after bind (set by <see cref="RunAsync"/>).</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    /// <summary>Completes once the socket is bound; faults when binding failed.</summary>
    public Task Ready => _ready.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        UdpClient socket;
        try
        {
            socket = new UdpClient(_bind);
        }
        catch (Exception ex)
        {
            _logger.ListenFailed(_bind.ToString(), ex.Message);
            _ready.TrySetException(ex);
            throw;
        }
        using (socket)
        {
            BoundEndpoint = (IPEndPoint)socket.Client.LocalEndPoint!;
            _ready.TrySetResult();
            _logger.LogInformation("listening {Name} on {Endpoint} (fins-udp)", Name, BoundEndpoint);

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult datagram;
                try
                {
                    datagram = await socket.ReceiveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    // e.g. ICMP port unreachable surfacing as ConnectionReset on Windows: not fatal
                    _logger.ConnError("udp", _bind.ToString(), ex.Message);
                    continue;
                }
                var remote = datagram.RemoteEndPoint.ToString();
                FinsOutcome outcome;
                try
                {
                    if (!FinsServer.TryHandle(datagram.Buffer, _device, _maps, out outcome))
                    {
                        _logger.MalformedFrame("udp", remote, $"bad FINS frame: {Convert.ToHexString(datagram.Buffer)}");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "device failure while handling a FINS request");
                    continue;
                }
                if (outcome.ResponseRequired)
                {
                    await socket.SendAsync(outcome.Response, datagram.RemoteEndPoint, ct);
                }
                _logger.Request(remote, outcome.Op, outcome.Target, outcome.Points,
                    outcome.EndCode == Fins.EndOk ? null : $"{outcome.EndCode:X4}");
            }
        }
    }
}
