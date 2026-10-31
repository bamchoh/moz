namespace Moz.Tests;

/// <summary>Config-level tests for [[client]] (phase 6).</summary>
public class ClientConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-clientcfg-" + Guid.NewGuid().ToString("N"));

    public ClientConfigTests() => Directory.CreateDirectory(_dir);

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
        name = "plc-mirror"

        [[client]]
        name = "p1"
        device = "plc-mirror"
        remote_addr = "192.168.0.10:502"
        remote_unit = 1
        interval_ms = 1000

        [[client.poll]]
        fc = 3
        remote_offset = 100
        count = 10
        local_offset = 0
        """;

    [Fact]

    public void ValidClient_Loads()
    {
        var result = Load("ok.toml", Base);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        var c = result.Config!.Client;
        Assert.Single(c);
        Assert.Equal("p1", c[0].Name);
        Assert.Equal("192.168.0.10:502", c[0].RemoteAddr);
        Assert.Equal(1, c[0].RemoteUnit);
        Assert.Equal(1000, c[0].IntervalMs);
        Assert.Single(c[0].Poll);
        Assert.Equal(3, c[0].Poll[0].Fc);
        Assert.Equal(100, c[0].Poll[0].RemoteOffset);
        Assert.Equal(10, c[0].Poll[0].Count);
        Assert.Equal(0, c[0].Poll[0].LocalOffset);
    }

    [Fact]
    public void MissingDeviceOrAddr_Fails()
    {
        var r1 = Load("nodev.toml", Base.Replace("device = \"plc-mirror\"\nremote_addr", "remote_addr"));
        Assert.False(r1.Ok);
        Assert.Contains(r1.Errors, e => e.Message.Contains("client.device is required"));

        var r2 = Load("noaddr.toml", Base.Replace("remote_addr = \"192.168.0.10:502\"", ""));
        Assert.False(r2.Ok);
        Assert.Contains(r2.Errors, e => e.Message.Contains("client.remote_addr is required"));
    }

    [Theory]
    [InlineData("192.168.0.10")]        // no port
    [InlineData("192.168.0.10:0")]      // port 0
    [InlineData("192.168.0.10:70000")]  // port too big
    public void BadRemoteAddr_Fails(string addr)
    {
        var result = Load("addr.toml", Base.Replace("remote_addr = \"192.168.0.10:502\"", $"remote_addr = \"{addr}\""));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("remote_addr value \"192.168.0.10"));
    }

    [Fact]
    public void UnknownDevice_Fails()
    {
        var result = Load("ref.toml", Base.Replace("device = \"plc-mirror\"", "device = \"nope\""));
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("references device \"nope\""));
    }

    [Fact]
    public void LocalKind_Derived_From_Fc_WhenAbsent()
    {
        var result = Load("derived.toml", Base);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        Assert.Equal("holding", result.Config!.Client[0].Poll[0].LocalKind);
    }

    [Theory]
    [InlineData(1, "holding", "requires a bit local kind")]
    [InlineData(3, "coil", "requires a word local kind")]
    public void LocalKind_Bitness_MustMatch_Fc(int fc, string localKind, string why)
    {
        var toml = Base.Replace("[[client.poll]]", $"[[client.poll]]\n        local_kind = \"{localKind}\"")
                       .Replace("fc = 3", $"fc = {fc}");
        var result = Load("bitness.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains(why));
    }

    [Theory]
    [InlineData(5)]   // write FC — reads only
    [InlineData(99)]
    [InlineData(0)]
    public void Poll_Fc_MustBeRead(byte fc)
    {
        var toml = Base.Replace("fc = 3", $"fc = {fc}");
        var result = Load("fc.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains($"poll.fc value {fc}"));
    }

    [Theory]
    [InlineData(0)]    // qty 0
    [InlineData(126)]  // > 125 (word fc)
    public void Poll_Count_Bounds(int count)
    {
        var toml = Base.Replace("count = 10", $"count = {count}");
        var result = Load("count.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains($"poll.count value {count}"));
    }

    [Fact]
    public void Poll_Count_BitFc_Uses2000Limit()
    {
        var toml = Base.Replace("fc = 3", "fc = 1").Replace("count = 10", "count = 2001");
        var result = Load("countbit.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("poll.count value 2001"));
    }

    [Fact]
    public void RemoteUnit_And_Interval_Bounds()
    {
        var toml = Base.Replace("remote_unit = 1", "remote_unit = 300");
        var result = Load("unit.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("remote_unit value 300"));

        var toml2 = Base.Replace("interval_ms = 1000", "interval_ms = 10");
        var result2 = Load("int.toml", toml2);
        Assert.False(result2.Ok);
        Assert.Contains(result2.Errors, e => e.Message.Contains("interval_ms value 10"));
    }

    [Fact]
    public void DuplicateClientName_Fails()
    {
        var toml = Base.Replace("[[client]]",
            "[[client]]\nname = \"p1\"\ndevice = \"plc-mirror\"\nremote_addr = \"1.2.3.4:502\"\n\n[[client]]");
        var result = Load("dupe.toml", toml);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Message.Contains("duplicate client name"));
    }
}