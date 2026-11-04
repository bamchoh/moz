using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moz.Devices;
using Moz.OpcUa;
using Opc.Ua;

namespace Moz.Tests;

/// <summary>
/// OPC UA server user management: named users with a password and read or
/// write access, and what anonymous sessions may do. Exercised through an
/// independent SDK client (<see cref="UaTestClient"/>).
/// </summary>
public class OpcUaAuthTests
{
    private static int s_port = 48620; // own range
    private static readonly SemaphoreSlim s_lock = new(1, 1);

    private sealed record Started(Device Device, VariableTable Variables, Task Run, CancellationTokenSource Cts, ushort Port) : IAsyncDisposable
    {
        public string Endpoint => $"opc.tcp://127.0.0.1:{Port}";

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Cts.Dispose();
        }
    }

    private static async Task<Started> StartAsync(OpcUaServerOptions options)
    {
        await s_lock.WaitAsync();
        try
        {
            var port = (ushort)Math.Clamp(Interlocked.Increment(ref s_port), 48621, 48699);
            var device = new Device("plc-ua", holdingRegisters: 100, coils: 100);
            var vars = new VariableTable(device, [new Variable("temp", TableKind.HoldingRegister, 0, VariableType.Real)], NullLogger.Instance);
            var runner = new OpcUaServerRunner("ua-auth", $"opc.tcp://127.0.0.1:{port}", device, vars, NullLogger.Instance, options);
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

    private static readonly OpcUaServerOptions TwoUsers = new(
        Users: [new OpcUaUser("operator", "op-pass", CanWrite: true), new OpcUaUser("viewer", "view-pass", CanWrite: false)],
        Anonymous: OpcUaAccess.Deny);

    private static IUserIdentity User(string name, string password) => new UserIdentity(name, Encoding.UTF8.GetBytes(password));

    private static float Float(DataValue dv) => dv.WrappedValue.TryGetValue(out float f) ? f : float.NaN;

    [Fact]
    public async Task NoUsers_AnonymousWrites_AsBefore()
    {
        await using var server = await StartAsync(OpcUaServerOptions.Default);
        using var session = await UaTestClient.ConnectAsync(server.Endpoint);
        var ns = await UaTestClient.MozNamespaceAsync(session);
        Assert.True(StatusCode.IsGood(await UaTestClient.WriteAsync(session, new NodeId("temp", ns), new Variant(1.5f))));
        Assert.Equal(1.5f, server.Variables.Read<float>("temp"));
    }

    [Fact]
    public async Task UsersDeclared_AnonymousIsRejected()
    {
        await using var server = await StartAsync(TwoUsers);
        var ex = await Assert.ThrowsAsync<ServiceResultException>(() => UaTestClient.ConnectAsync(server.Endpoint));
        Assert.True(ex.StatusCode == StatusCodes.BadUserAccessDenied || ex.StatusCode == StatusCodes.BadIdentityTokenRejected || ex.StatusCode == StatusCodes.BadIdentityTokenInvalid,
            $"got {ex.StatusCode}");
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        await using var server = await StartAsync(TwoUsers);
        var ex = await Assert.ThrowsAsync<ServiceResultException>(() => UaTestClient.ConnectAsync(server.Endpoint, User("operator", "nope")));
        Assert.True(ex.StatusCode == StatusCodes.BadUserAccessDenied || ex.StatusCode == StatusCodes.BadIdentityTokenRejected,
            $"got {ex.StatusCode}");
        var ex2 = await Assert.ThrowsAsync<ServiceResultException>(() => UaTestClient.ConnectAsync(server.Endpoint, User("nobody", "op-pass")));
        Assert.True(ex2.StatusCode == StatusCodes.BadUserAccessDenied || ex2.StatusCode == StatusCodes.BadIdentityTokenRejected, $"got {ex2.StatusCode}");
    }

    [Fact]
    public async Task WriteUser_CanReadAndWrite()
    {
        await using var server = await StartAsync(TwoUsers);
        using var session = await UaTestClient.ConnectAsync(server.Endpoint, User("operator", "op-pass"));
        var ns = await UaTestClient.MozNamespaceAsync(session);
        Assert.True(StatusCode.IsGood(await UaTestClient.WriteAsync(session, new NodeId("temp", ns), new Variant(7.5f))));
        Assert.Equal(7.5f, server.Variables.Read<float>("temp"));
        Assert.Equal(7.5f, Float(await UaTestClient.ReadAsync(session, new NodeId("temp", ns))));
    }

    [Fact]
    public async Task ReadUser_CanRead_WriteIsDenied()
    {
        await using var server = await StartAsync(TwoUsers);
        server.Variables.Write("temp", 3.25f);
        using var session = await UaTestClient.ConnectAsync(server.Endpoint, User("viewer", "view-pass"));
        var ns = await UaTestClient.MozNamespaceAsync(session);
        Assert.Equal(3.25f, Float(await UaTestClient.ReadAsync(session, new NodeId("temp", ns))));
        var status = await UaTestClient.WriteAsync(session, new NodeId("temp", ns), new Variant(9.0f));
        Assert.Equal(StatusCodes.BadUserAccessDenied, status);
        Assert.Equal(3.25f, server.Variables.Read<float>("temp"));
    }

    [Fact]
    public async Task AnonymousRead_Configured_ReadsButCannotWrite()
    {
        await using var server = await StartAsync(TwoUsers with { Anonymous = OpcUaAccess.Read });
        server.Variables.Write("temp", 2.0f);
        using var session = await UaTestClient.ConnectAsync(server.Endpoint);
        var ns = await UaTestClient.MozNamespaceAsync(session);
        Assert.Equal(2.0f, Float(await UaTestClient.ReadAsync(session, new NodeId("temp", ns))));
        Assert.Equal(StatusCodes.BadUserAccessDenied, await UaTestClient.WriteAsync(session, new NodeId("temp", ns), new Variant(5.0f)));
    }
}
