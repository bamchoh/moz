namespace Moz.Tests;

/// <summary>
/// RED-GREEN-REFACTOR for phase 2: `moz validate <config>` subcommand.
/// The same loader is reused — there is exactly one validation code path.
/// </summary>
public class CliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-clitests-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _stderr = new();
    private readonly StringWriter _stdout = new();
    private readonly TextWriter _origError = Console.Error;
    private readonly TextWriter _origOut = Console.Out;

    public CliTests()
    {
        Directory.CreateDirectory(_dir);
        Console.SetError(_stderr);
        Console.SetOut(_stdout);
    }

    public void Dispose()
    {
        Console.SetError(_origError);
        Console.SetOut(_origOut);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string ValidT =
        """
        [[device]]
        name = "plc1"

        [[server]]
        name = "sim"
        protocol = "modbus-tcp"
        device = "plc1"
        port = 50200
        """;

    [Fact]
    public async Task Validate_GoodConfig_Exits0()
    {
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("good.toml", ValidT)]);
        Assert.Equal(0, exit);
        Assert.Equal("", _stderr.ToString()); // success prints nothing
    }

    [Fact]
    public async Task Validate_BadConfig_Exits2_AndListsErrors()
    {
        var toml = ValidT.Replace("port = 50200", "port = 50200\nprotocl = \"x\"\nportx = 1");
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("bad.toml", toml)]);
        Assert.Equal(2, exit);
        var stderr = _stderr.ToString();
        Assert.Contains("unknown key 'protocl'", stderr);
        Assert.Contains("unknown key 'portx'", stderr);
        Assert.Matches("bad\\.toml", stderr); // errors carry the file location
    }

    [Fact]
    public async Task Validate_MissingFile_Exits2()
    {
        var exit = await Moz.Cli.CliApp.Run(["validate", Path.Combine(_dir, "nope.toml")]);
        Assert.Equal(2, exit);
        Assert.Contains("cannot read file", _stderr.ToString());
    }

    [Fact]
    public async Task NoArgs_Exits2_WithUsage()
    {
        Assert.Equal(2, await Moz.Cli.CliApp.Run([]));
        Assert.Contains("usage:", _stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_WithoutConfigPath_Exits2()
    {
        Assert.Equal(2, await Moz.Cli.CliApp.Run(["validate"]));
    }

    [Fact]
    public async Task Help_Exits0()
    {
        Assert.Equal(0, await Moz.Cli.CliApp.Run(["--help"]));
        Assert.Contains("usage:", _stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("validate", _stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Validate_OpcUaClient_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "mirror"
            words = 100

            [[variable]]
            name = "temperature"
            device = "mirror"
            kind = "words"
            offset = 0
            type = "real"

            [[client]]
            name = "ua-client"
            protocol = "opcua"
            device = "mirror"
            endpoint = "opc.tcp://192.168.0.30:4840"

            [[client.poll]]
            node = "nsu=urn:moz:ua:ns;s=temp"
            variable = "temperature"
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("ua-client.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_McServer_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[server]]
            name = "mc-a"
            protocol = "mc-3e"
            device = "plc1"
            port = 5000

            [[server.map]]
            device = "W"
            kind = "words"
            offset = 10000
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("mc-server.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_FinsServers_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[server]]
            name = "fins-a"
            protocol = "fins-tcp"
            device = "plc1"
            port = 9600
            node = 10

            [[server.map]]
            device = "CIO"
            kind = "words"
            offset = 10000
            count = 100

            [[server]]
            name = "fins-b"
            protocol = "fins-udp"
            device = "plc1"
            port = 9600
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("fins-server.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_KvServer_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[server]]
            name = "kv-a"
            protocol = "kv-hostlink"
            device = "plc1"
            port = 8501

            [[server.map]]
            device = "MR"
            kind = "bits"
            offset = 10000
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("kv-server.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_S7Server_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[server]]
            name = "s7-a"
            protocol = "s7"
            device = "plc1"
            port = 102

            [[server.map]]
            device = "M"
            kind = "words"
            offset = 10000
            count = 256
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("s7-server.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_EnipServer_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"

            [[variable]]
            name = "temp"
            device = "plc1"
            kind = "words"
            offset = 0
            type = "real"

            [[server]]
            name = "enip-a"
            protocol = "enip"
            device = "plc1"
            port = 44818
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("enip-server.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_McClient_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "mirror"
            bits = 100
            words = 100

            [[client]]
            name = "mc-master"
            protocol = "mc-3e"
            device = "mirror"
            remote_addr = "192.168.0.20:5000"

            [[client.poll]]
            remote = "D100"
            count = 4
            local_offset = 0

            [[client.push]]
            remote = "M0"
            count = 16
            local_offset = 0
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("mc-client.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_RtuClient_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "mirror"
            words = 100

            [[client]]
            name = "rtu-master"
            protocol = "modbus-rtu"
            device = "mirror"
            serial_port = "COM11"
            baud = 19200

            [[client.poll]]
            fc = 3
            remote_offset = 0
            count = 4
            local_offset = 0
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("rtu-client.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_WithInitialValues_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"
            words = 100
            read_only_words = 100

            [[variable]]
            name = "setpoint"
            device = "plc1"
            kind = "read_only_words"
            offset = 0
            type = "real"
            value = 20.5

            [[server]]
            name = "sim"
            protocol = "modbus-tcp"
            device = "plc1"
            port = 50200
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("values.toml", toml)]);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task Validate_StructConfig_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "plc1"
            words = 100

            [[struct]]
            name = "Motor"
            [[struct.field]]
            name = "speed"
            type = "int"
            [[struct.field]]
            name = "on"
            type = "bool"

            [[variable]]
            name = "motor"
            device = "plc1"
            kind = "words"
            offset = 0
            type = "Motor"
            count = 2

            [[server]]
            name = "sim"
            protocol = "modbus-tcp"
            device = "plc1"
            port = 50200
            """;
        var exit = await Moz.Cli.CliApp.Run(["validate", WriteFile("struct.toml", toml)]);
        Assert.Equal(0, exit);
    }
}
