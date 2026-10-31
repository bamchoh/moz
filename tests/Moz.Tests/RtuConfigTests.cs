namespace Moz.Tests;

/// <summary>Config-level tests for [[server]] protocol="modbus-rtu" (phase 5).</summary>
public class RtuConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-rtucfg-" + Guid.NewGuid().ToString("N"));

    public RtuConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string Base =
        """
        [[device]]
        name = "plc1"

        [[server]]
        name = "rtu-a"
        protocol = "modbus-rtu"
        device = "plc1"
        serial_port = "COM10"
        """;

    [Fact]
    public void ValidRtuConfig_Loads()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("ok.toml", Base));
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var s = result.Config!.Server[0];
        Assert.Equal("modbus-rtu", s.Protocol);
        Assert.Equal("COM10", s.SerialPort);
        Assert.Equal(9600, s.Baud);
        Assert.Equal("even", s.Parity);   // Modbus serial default 8E1
        Assert.Equal(8, s.DataBits);
        Assert.Equal(1, s.StopBits);
    }

    [Fact]
    public void MissingSerialPort_Fails()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("noport.toml",
            Base.Replace("serial_port = \"COM10\"", "")));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("serial_port is required"));
    }

    [Fact]
    public void BadParity_Fails()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("parity.toml",
            Base.Replace("serial_port = \"COM10\"", "serial_port = \"COM10\"\nparity = \"noneX\"")));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("parity value \"noneX\""));
    }

    [Fact]
    public void BadBaud_Fails()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("baud.toml",
            Base.Replace("serial_port = \"COM10\"", "serial_port = \"COM10\"\nbaud = 0")));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("baud value 0"));
    }

    [Fact]
    public void BadDataBits_Fails()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("db.toml",
            Base.Replace("serial_port = \"COM10\"", "serial_port = \"COM10\"\ndata_bits = 5")));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("data_bits value 5"));
    }

    [Fact]
    public void BadStopBits_Fails()
    {
        var result = Moz.Config.ConfigLoader.Load(WriteFile("sb.toml",
            Base.Replace("serial_port = \"COM10\"", "serial_port = \"COM10\"\nstop_bits = 3")));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("stop_bits value 3"));
    }

    [Fact]
    public void SerialPortOnNonSerialProtocol_Fails()
    {
        var toml =
            """
            [[device]]
            name = "plc1"

            [[server]]
            name = "tcp-a"
            protocol = "modbus-tcp"
            device = "plc1"
            serial_port = "COM10"
            """;
        var result = Moz.Config.ConfigLoader.Load(WriteFile("mismatch.toml", toml));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("serial_port"));
    }
}