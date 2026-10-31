namespace Moz.Tests;

/// <summary>Config-level tests for [[server]] protocol="opcua" (phase 7).</summary>
public class OpcUaConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-uacfg-" + Guid.NewGuid().ToString("N"));

    public OpcUaConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Moz.Config.ConfigResult Load(string name, string toml)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, toml);
        return Moz.Config.ConfigLoader.Load(path);
    }

    private const string Base =
        """
        [[device]]
        name = "plc1"

        [[server]]
        name = "ua-a"
        protocol = "opcua"
        device = "plc1"
        endpoint = "opc.tcp://127.0.0.1:48400"
        """;

    [Fact]
    public void ValidOpcUa_Loads()
    {
        var r = Load("ok.toml", Base);
        Assert.True(r.Ok, string.Join("\n", r.Errors));
        Assert.Equal("opc.tcp://127.0.0.1:48400", r.Config!.Server[0].Endpoint);
    }

    [Fact]
    public void MissingEndpoint_Fails()
    {
        var r = Load("noep.toml", Base.Replace("endpoint = \"opc.tcp://127.0.0.1:48400\"", ""));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Message.Contains("server.endpoint is required"));
    }

    [Theory]
    [InlineData("opc.tcp://127.0.0.1")]         // no port
    [InlineData("opc.tcp://127.0.0.1:0")]       // port 0
    [InlineData("opc.tcp://127.0.0.1:99999")]
    [InlineData("http://127.0.0.1:48400")]      // wrong scheme
    [InlineData("127.0.0.1:48400")]             // not a URL
    [InlineData("opc.tcp://:48400")]            // no host
    public void InvalidEndpoint_Fails(string endpoint)
    {
        var r = Load("bad.toml", Base.Replace("endpoint = \"opc.tcp://127.0.0.1:48400\"", $"endpoint = \"{endpoint}\""));
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Message.Contains($"endpoint value \"{endpoint}\" is invalid"));
    }

    [Fact]
    public void EndpointOnTcpProtocol_Fails()
    {
        var toml =
            """
            [[device]]
            name = "plc1"

            [[server]]
            name = "tcp-a"
            protocol = "modbus-tcp"
            device = "plc1"
            endpoint = "opc.tcp://127.0.0.1:48400"
            """;
        var r = Load("mismatch.toml", toml);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors, e => e.Message.Contains("only valid for the opcua protocol"));
    }
}