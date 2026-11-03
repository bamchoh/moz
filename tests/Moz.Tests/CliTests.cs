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
    public async Task Validate_RtuClient_Exits0()
    {
        const string toml =
            """
            [[device]]
            name = "mirror"
            holding = 100

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
            holding = 100
            input = 100

            [[variable]]
            name = "setpoint"
            device = "plc1"
            kind = "input"
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
            holding = 100

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
            kind = "holding"
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
