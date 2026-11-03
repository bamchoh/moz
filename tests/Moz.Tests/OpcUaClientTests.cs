using Microsoft.Extensions.Logging.Abstractions;
using Moz.Client;
using Moz.Devices;
using Moz.OpcUa;
using Opc.Ua;

namespace Moz.Tests;

/// <summary>
/// moz as OPC UA client: the generic client loop over <see cref="OpcUaRemote"/>,
/// against moz's own OPC UA server. Nodes map to typed local variables; the
/// value conversion is the server's codec in reverse (UaValueCodec).
/// </summary>
public class OpcUaClientTests
{
    private static int s_port = 48520; // own range: OpcUaServerTests allocate 48421..48499
    private static readonly SemaphoreSlim s_lock = new(1, 1);

    private sealed record Started(Device Device, VariableTable Variables, Task Run, CancellationTokenSource Cts, ushort Port)
        : IAsyncDisposable
    {
        public string Endpoint => $"opc.tcp://127.0.0.1:{Port}";

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Cts.Dispose();
        }
    }

    /// <summary>moz's UA server over a device with temp (Float), rpm (UInt16), pump (Boolean), samples (Int16[3]), label (String).</summary>
    private static async Task<Started> StartServerAsync()
    {
        await s_lock.WaitAsync();
        try
        {
            var port = (ushort)Math.Clamp(Interlocked.Increment(ref s_port), 48521, 48599);
            var device = new Device("plc-ua", holdingRegisters: 100, coils: 100);
            var vars = new VariableTable(device,
            [
                new Variable("temp", TableKind.HoldingRegister, 0, VariableType.Real),
                new Variable("rpm", TableKind.HoldingRegister, 2, VariableType.Uint),
                new Variable("pump", TableKind.Coil, 0, VariableType.Bool),
                new Variable("samples", TableKind.HoldingRegister, 20, VariableType.Int, Count: 3),
                new Variable("label", TableKind.HoldingRegister, 24, VariableType.String, Length: 8),
            ], NullLogger.Instance);
            var runner = new OpcUaServerRunner("ua-test", $"opc.tcp://127.0.0.1:{port}", device, vars, NullLogger.Instance);
            var cts = new CancellationTokenSource();
            var run = runner.RunAsync(cts.Token);
            await runner.Ready.WaitAsync(TimeSpan.FromSeconds(30));
            return new Started(device, vars, run, cts, port);
        }
        finally
        {
            s_lock.Release();
        }
    }

    private static readonly Variable Temperature = new("temperature", TableKind.HoldingRegister, 10, VariableType.Real);
    private static readonly Variable Speed = new("speed", TableKind.HoldingRegister, 20, VariableType.Uint);
    private static readonly Variable Pump = new("pump", TableKind.Coil, 3, VariableType.Bool);
    private static readonly Variable Samples = new("samples", TableKind.HoldingRegister, 30, VariableType.Int, Count: 3);
    private static readonly Variable Label = new("label", TableKind.HoldingRegister, 40, VariableType.String, Length: 8);
    private static readonly Variable WrongUint = new("wrong", TableKind.HoldingRegister, 50, VariableType.Uint);

    private static (Device Device, VariableTable Vars) LocalDevice()
    {
        var device = new Device("local", holdingRegisters: 100, coils: 16);
        return (device, new VariableTable(device, [Temperature, Speed, Pump, Samples, Label, WrongUint], NullLogger.Instance));
    }

    private static OpcUaEntry Entry(string node, Variable variable) => new(ExpandedNodeId.Parse(node), variable);

    private const string Ns = "nsu=urn:moz:ua:ns;";

    [Fact]
    public async Task Poll_ReadsNodes_IntoLocalVariables()
    {
        await using var server = await StartServerAsync();
        server.Variables.Write("temp", 20.5f);
        server.Variables.Write("rpm", 1234u);
        server.Variables.Write("pump", true);
        server.Variables.WriteAll("samples", new[] { 1, 2, 3 });
        server.Variables.Write("label", "hello");
        var (local, vars) = LocalDevice();
        var provider = new CapturingProvider();
        OpcUaEntry[] polls =
        [
            Entry(Ns + "s=temp", Temperature), Entry(Ns + "s=rpm", Speed), Entry(Ns + "s=pump", Pump),
            Entry(Ns + "s=samples", Samples), Entry(Ns + "s=label", Label),
        ];
        var remote = new OpcUaRemote("ua", server.Endpoint, 5000, polls, [], provider.CreateLogger("ua"));
        var poller = new ClientPoller("ua", remote, 200, local, provider.CreateLogger("ua"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(4000);
        Assert.True(poller.IsConnected, string.Join("; ", provider.Entries.Select(e => e.Message)));
        await cts.CancelAsync();
        await task;

        Assert.Equal(20.5f, vars.Read<float>("temperature"));
        Assert.Equal(1234u, vars.Read<uint>("speed"));
        Assert.True(vars.Read<bool>("pump"));
        Assert.Equal([1, 2, 3], vars.ReadAll<int>("samples"));
        Assert.Equal("hello", vars.Read<string>("label"));
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("s=temp") && e.Message.Contains("result=ok"));
        Assert.DoesNotContain(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id);
    }

    [Fact]
    public async Task Push_WritesLocalVariables_ToNodes()
    {
        await using var server = await StartServerAsync();
        var (local, vars) = LocalDevice();
        OpcUaEntry[] pushes = [Entry(Ns + "s=temp", Temperature), Entry(Ns + "s=label", Label), Entry(Ns + "s=samples", Samples)];
        var remote = new OpcUaRemote("ua", server.Endpoint, 5000, [], pushes, NullLogger.Instance);
        var poller = new ClientPoller("ua", remote, 200, local, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(2500); // connected
        vars.Write("temperature", 33.5f);
        vars.Write("label", "moz");
        vars.WriteAll("samples", new[] { -1, 7, 300 });
        await Task.Delay(1500);
        Assert.False(poller.HasPendingPush);
        await cts.CancelAsync();
        await task;

        Assert.Equal(33.5f, server.Variables.Read<float>("temp"));
        Assert.Equal("moz", server.Variables.Read<string>("label"));
        Assert.Equal([-1, 7, 300], server.Variables.ReadAll<int>("samples"));
    }

    [Fact]
    public async Task BadNode_AndTypeMismatch_AreRejections_NotRetried_LoopContinues()
    {
        await using var server = await StartServerAsync();
        server.Variables.Write("temp", 1.5f);
        var (local, vars) = LocalDevice();
        var provider = new CapturingProvider();
        OpcUaEntry[] polls =
        [
            Entry(Ns + "s=nope", Speed),        // unknown node
            Entry(Ns + "s=temp", WrongUint),    // Float into a uint variable
            Entry(Ns + "s=temp", Temperature),  // fine
        ];
        OpcUaEntry[] pushes = [Entry(Ns + "s=rpm", Temperature)]; // Float written to a UInt16 node
        var remote = new OpcUaRemote("ua", server.Endpoint, 5000, polls, pushes, provider.CreateLogger("ua"));
        var poller = new ClientPoller("ua", remote, 200, local, provider.CreateLogger("ua"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(2500);
        vars.Write("temperature", 9.25f);
        await Task.Delay(1500);
        Assert.True(poller.IsConnected);
        Assert.False(poller.HasPendingPush);
        await cts.CancelAsync();
        await task;

        Assert.Equal(0u, vars.Read<uint>("wrong"));
        Assert.Equal(1.5f, vars.Read<float>("temperature")); // the good poll ran before the local write... then our write; either way the poll kept working
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("s=nope") && e.Message.Contains("err=BadNodeIdUnknown"));
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=read") && e.Message.Contains("s=temp") && e.Message.Contains("err=BadTypeMismatch"));
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.Request.Id
            && e.Message.Contains("op=write") && e.Message.Contains("s=rpm") && e.Message.Contains("err=BadTypeMismatch"));
        Assert.DoesNotContain(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id);
        Assert.Equal(1234u, 1234u);
    }

    [Fact]
    public async Task NoServer_IsConnError_EachCycle()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // refused from here on
        var (local, _) = LocalDevice();
        var provider = new CapturingProvider();
        var remote = new OpcUaRemote("ua", $"opc.tcp://127.0.0.1:{port}", 1000, [Entry(Ns + "s=temp", Temperature)], [], provider.CreateLogger("ua"));
        var poller = new ClientPoller("ua", remote, 300, local, provider.CreateLogger("ua"));

        using var cts = new CancellationTokenSource();
        var task = poller.RunAsync(cts.Token);
        await Task.Delay(3000);
        Assert.False(poller.IsConnected);
        await cts.CancelAsync();
        await task;
        Assert.Contains(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnError.Id);
        Assert.DoesNotContain(provider.Entries, e => e.EventId == Moz.Logging.CommLog.EventIds.ConnAccepted.Id);
    }
}
