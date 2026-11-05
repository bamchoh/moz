using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.EtherNetIp;

/// <summary>
/// EtherNet/IP encapsulation: a 24-byte little-endian header (command,
/// length, session handle, status, sender context, options) followed by
/// command data. Explicit messaging rides in SendRRData (unconnected, CPF
/// null address + unconnected data item B2) and SendUnitData (connected,
/// CPF connected address A1 + connected data item B1 with a sequence count).
/// </summary>
public static class Encap
{
    public const int HeaderSize = 24;
    public const int DataMax = 8192;

    public const ushort Nop = 0x0000;
    public const ushort ListServices = 0x0004;
    public const ushort ListIdentity = 0x0063;
    public const ushort ListInterfaces = 0x0064;
    public const ushort RegisterSession = 0x0065;
    public const ushort UnRegisterSession = 0x0066;
    public const ushort SendRRData = 0x006F;
    public const ushort SendUnitData = 0x0070;

    public const uint StatusOk = 0;
    public const uint StatusInvalidCommand = 0x01;
    public const uint StatusInvalidSession = 0x64;
    public const uint StatusInvalidLength = 0x65;
    public const uint StatusUnsupportedVersion = 0x69;

    public const ushort ItemNullAddress = 0x0000;
    public const ushort ItemConnectedAddress = 0x00A1;
    public const ushort ItemConnectedData = 0x00B1;
    public const ushort ItemUnconnectedData = 0x00B2;

    /// <summary>Command, data length (within the limit), session and status of a header.</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> header, out ushort command, out int length, out uint session, out uint status)
    {
        command = 0;
        length = 0;
        session = 0;
        status = 0;
        if (header.Length < HeaderSize)
        {
            return false;
        }
        command = BinaryPrimitives.ReadUInt16LittleEndian(header);
        length = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        session = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        status = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        return length <= DataMax;
    }

    public static byte[] Build(ushort command, uint session, uint status, ReadOnlySpan<byte> context, ReadOnlySpan<byte> data)
    {
        var frame = new byte[HeaderSize + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), session);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), status);
        context[..Math.Min(8, context.Length)].CopyTo(frame.AsSpan(12));
        data.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>The items of a common packet format block (after the 6-byte interface handle + timeout of SendRRData / SendUnitData).</summary>
    public static bool TryParseItems(ReadOnlySpan<byte> cpf, out List<(ushort Type, byte[] Data)> items)
    {
        items = [];
        if (cpf.Length < 2)
        {
            return false;
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(cpf);
        int at = 2;
        for (int i = 0; i < count; i++)
        {
            if (at + 4 > cpf.Length)
            {
                return false;
            }
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(cpf[at..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(cpf[(at + 2)..]);
            if (at + 4 + length > cpf.Length)
            {
                return false;
            }
            items.Add((type, cpf.Slice(at + 4, length).ToArray()));
            at += 4 + length;
        }
        return true;
    }

    /// <summary>Interface handle 0, timeout 0, then the items.</summary>
    public static byte[] BuildItems(params (ushort Type, byte[] Data)[] items)
    {
        var bytes = new List<byte>(new byte[6]);
        bytes.AddRange([(byte)items.Length, (byte)(items.Length >> 8)]);
        foreach (var (type, data) in items)
        {
            bytes.AddRange([(byte)type, (byte)(type >> 8), (byte)data.Length, (byte)(data.Length >> 8)]);
            bytes.AddRange(data);
        }
        return bytes.ToArray();
    }
}

/// <summary>
/// EtherNet/IP explicit messaging server (the real units listen on 44818) —
/// one <see cref="IProtocolRunner"/> per [[server]] with protocol "enip".
/// The device's typed variables are the tags (<see cref="CipRouter"/>).
/// Each TCP connection owns one session handle and its class 3 connections;
/// a frame that is not an encapsulation header closes the connection
/// without an answer.
/// </summary>
public sealed class EnipServer : IProtocolRunner
{
    /// <summary>CIP reply budget of an unconnected (SendRRData) message: the Logix unconnected limit.</summary>
    public const int UnconnectedReplyMax = 504;

    private static int s_session;
    private static int s_connection;

    private readonly IPEndPoint _bind;
    private readonly VariableTable _table;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EnipServer(string name, IPEndPoint bind, VariableTable table, ILogger logger)
    {
        Name = name;
        _bind = bind;
        _table = table;
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
        _logger.LogInformation("listening {Name} on {Endpoint} (enip, {Count} tags)", Name, BoundEndpoint, _table.Variables.Count);

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

    /// <summary>The class 3 connections of one TCP connection.</summary>
    private sealed class Connections : ICipConnections
    {
        public readonly Dictionary<uint, (uint ToOriginator, ushort Serial, ushort Vendor, uint OriginatorSerial, int Size)> ByTarget = [];

        public uint OpenConnection(uint toOriginatorId, ushort serial, ushort vendor, uint originatorSerial, int size)
        {
            uint id = 0x4D4F0000u + (uint)Interlocked.Increment(ref s_connection);
            ByTarget[id] = (toOriginatorId, serial, vendor, originatorSerial, size);
            return id;
        }

        public bool CloseConnection(ushort serial, ushort vendor, uint originatorSerial)
        {
            foreach (var (id, c) in ByTarget)
            {
                if (c.Serial == serial && c.Vendor == vendor && c.OriginatorSerial == originatorSerial)
                {
                    ByTarget.Remove(id);
                    return true;
                }
            }
            return false;
        }
    }

    private async Task ServeAsync(NetworkStream stream, string connId, string remote, CancellationToken ct)
    {
        uint session = 0;
        var connections = new Connections();
        var header = new byte[Encap.HeaderSize];
        var data = new byte[Encap.DataMax];
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
            if (!Encap.TryParseHeader(header, out var command, out var length, out var requestSession, out _))
            {
                _logger.MalformedFrame(connId, remote, $"bad encapsulation header: {Convert.ToHexString(header)}");
                return; // close without an answer
            }
            try
            {
                await stream.ReadExactlyAsync(data.AsMemory(0, length), ct);
            }
            catch (EndOfStreamException)
            {
                _logger.MalformedFrame(connId, remote, "truncated frame (peer closed mid-frame)");
                return;
            }
            var context = header.AsSpan(12, 8);
            var body = data.AsSpan(0, length);
            byte[]? response;
            switch (command)
            {
                case Encap.Nop:
                    response = null;
                    break;

                case Encap.ListIdentity:
                    response = Encap.Build(command, requestSession, Encap.StatusOk, context, ListIdentity());
                    _logger.Request(connId, "list-identity", "-", 0, null);
                    break;

                case Encap.ListServices:
                    response = Encap.Build(command, requestSession, Encap.StatusOk, context, ListServices());
                    break;

                case Encap.ListInterfaces:
                    response = Encap.Build(command, requestSession, Encap.StatusOk, context, [0, 0]);
                    break;

                case Encap.RegisterSession:
                    if (body.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(body) != 1)
                    {
                        response = Encap.Build(command, 0, Encap.StatusUnsupportedVersion, context, body.Length >= 4 ? body[..4] : body);
                        break;
                    }
                    session = (uint)Interlocked.Increment(ref s_session);
                    response = Encap.Build(command, session, Encap.StatusOk, context, body[..4]);
                    _logger.Request(connId, "register", $"session={session}", 0, null);
                    break;

                case Encap.UnRegisterSession:
                    _logger.Request(connId, "unregister", $"session={requestSession}", 0, null);
                    return;

                case Encap.SendRRData:
                case Encap.SendUnitData:
                    if (session == 0 || requestSession != session)
                    {
                        response = Encap.Build(command, requestSession, Encap.StatusInvalidSession, context, []);
                        _logger.Request(connId, command == Encap.SendRRData ? "rr-data" : "unit-data", $"session={requestSession}", 0, "64");
                        break;
                    }
                    if (body.Length < 6 || !Encap.TryParseItems(body[6..], out var items))
                    {
                        _logger.MalformedFrame(connId, remote, $"bad common packet format: {Convert.ToHexString(body)}");
                        return;
                    }
                    response = command == Encap.SendRRData
                        ? Unconnected(items, session, context, connId, connections)
                        : Connected(items, session, context, connId, connections);
                    if (response is null)
                    {
                        _logger.MalformedFrame(connId, remote, $"unexpected items: {string.Join(",", items.Select(i => i.Type.ToString("X4")))}");
                        return;
                    }
                    break;

                default:
                    response = Encap.Build(command, requestSession, Encap.StatusInvalidCommand, context, []);
                    _logger.Request(connId, $"cmd={command:X4}", "-", 0, "01");
                    break;
            }
            if (response is not null)
            {
                await stream.WriteAsync(response, ct);
            }
        }
    }

    private byte[]? Unconnected(List<(ushort Type, byte[] Data)> items, uint session, ReadOnlySpan<byte> context, string connId, Connections connections)
    {
        var request = items.FirstOrDefault(i => i.Type == Encap.ItemUnconnectedData).Data;
        if (request is null)
        {
            return null;
        }
        var outcome = Route(request, connections, UnconnectedReplyMax);
        _logger.Request(connId, outcome.Op, outcome.Target, outcome.Points, outcome.Error);
        return Encap.Build(Encap.SendRRData, session, Encap.StatusOk, context,
            Encap.BuildItems((Encap.ItemNullAddress, []), (Encap.ItemUnconnectedData, outcome.Reply)));
    }

    private byte[]? Connected(List<(ushort Type, byte[] Data)> items, uint session, ReadOnlySpan<byte> context, string connId, Connections connections)
    {
        var address = items.FirstOrDefault(i => i.Type == Encap.ItemConnectedAddress).Data;
        var payload = items.FirstOrDefault(i => i.Type == Encap.ItemConnectedData).Data;
        if (address is null || address.Length != 4 || payload is null || payload.Length < 2)
        {
            return null;
        }
        uint toTarget = BinaryPrimitives.ReadUInt32LittleEndian(address);
        if (!connections.ByTarget.TryGetValue(toTarget, out var connection))
        {
            return null;
        }
        var sequence = payload.AsSpan(0, 2);
        var outcome = Route(payload.AsSpan(2), connections, Math.Max(64, connection.Size - 2));
        _logger.Request(connId, outcome.Op, outcome.Target, outcome.Points, outcome.Error);
        var replyAddress = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(replyAddress, connection.ToOriginator);
        return Encap.Build(Encap.SendUnitData, session, Encap.StatusOk, context,
            Encap.BuildItems((Encap.ItemConnectedAddress, replyAddress), (Encap.ItemConnectedData, [.. sequence, .. outcome.Reply])));
    }

    private CipOutcome Route(ReadOnlySpan<byte> request, Connections connections, int maxReply)
    {
        try
        {
            return CipRouter.Handle(request, new CipContext(_table, Name, connections, maxReply));
        }
        catch (Exception ex)
        {
            // the "chute": an unexpected device failure must not kill the connection
            _logger.LogWarning(ex, "device failure while handling a CIP request");
            return new CipOutcome([0x80, 0, 0x0C, 0], "cmd=??", "?", 0, "0C"); // object state conflict
        }
    }

    /// <summary>ListIdentity item: encapsulation version, socket address, the identity attributes, state.</summary>
    private byte[] ListIdentity()
    {
        var identity = CipRouter.IdentityAttributes(new CipContext(_table, Name, new Connections(), UnconnectedReplyMax));
        var socket = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(socket, 2);                               // AF_INET
        BinaryPrimitives.WriteUInt16BigEndian(socket.AsSpan(2), (ushort)(BoundEndpoint?.Port ?? 44818));
        var item = new List<byte> { 1, 0 };                                              // protocol version
        item.AddRange(socket);
        item.AddRange(identity);
        item.Add(3);                                                                     // state: operational
        return [1, 0, 0x0C, 0x00, (byte)item.Count, (byte)(item.Count >> 8), .. item];
    }

    /// <summary>ListServices item: "Communications", version 1, CIP over TCP.</summary>
    private static byte[] ListServices()
    {
        var name = new byte[16];
        Encoding.ASCII.GetBytes("Communications", name);
        return [1, 0, 0x00, 0x01, 20, 0, 1, 0, 0x20, 0x00, .. name];
    }
}
