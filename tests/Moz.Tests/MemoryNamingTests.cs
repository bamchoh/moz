using Moz.Devices;

namespace Moz.Tests;

/// <summary>
/// Phase 20: the memory model is named protocol-neutrally — bits /
/// read_only_bits / words / read_only_words in the config (and
/// bits / readOnlyBits / words / readOnlyWords in scripts), Bits /
/// ReadOnlyBits / Words / ReadOnlyWords in code. The Modbus words (coils,
/// discrete, holding, input) are no longer accepted anywhere.
/// </summary>
public class MemoryNamingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-naming-" + Guid.NewGuid().ToString("N"));

    public MemoryNamingTests() => Directory.CreateDirectory(_dir);

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

    private static string Messages(Moz.Config.ConfigResult result) => string.Join("\n", result.Errors.Select(e => e.Message));

    private const string Base =
        """
        [[device]]
        name = "plc"
        bits = 10
        read_only_bits = 20
        words = 30
        read_only_words = 40

        [[variable]]
        name = "temp"
        device = "plc"
        kind = "words"
        offset = 0
        type = "real"

        [[variable]]
        name = "flag"
        device = "plc"
        kind = "read_only_bits"
        offset = 0
        type = "bool"

        [[server]]
        name = "mc"
        protocol = "mc-3e"
        device = "plc"
        port = 0

        [[server.map]]
        device = "W"
        kind = "words"
        offset = 10

        [[client]]
        name = "m"
        protocol = "modbus-tcp"
        device = "plc"
        remote_addr = "192.168.0.1:502"

        [[client.poll]]
        fc = 1
        remote_offset = 0
        count = 8
        local_kind = "bits"
        local_offset = 0
        """;

    // The Modbus spellings are built from pieces so that a vocabulary rename script cannot rewrite them.
    private static readonly string Coils = "coil" + "s", Discrete = "discr" + "ete", Holding = "hold" + "ing", Input = "in" + "put", Coil = "co" + "il";

    [Fact]
    public void NeutralVocabulary_Loads()
    {
        var r = Load("neutral.toml", Base);
        Assert.True(r.Ok, Messages(r));
        var d = r.Config!.Device[0];
        Assert.Equal((10, 20, 30, 40), (d.Bits, d.ReadOnlyBits, d.Words, d.ReadOnlyWords));
        Assert.Equal(("words", "read_only_bits", "words", "bits"),
            (r.Config.Variable[0].Kind, r.Config.Variable[1].Kind, r.Config.Server[0].Map[0].Kind, r.Config.Client[0].Poll[0].LocalKind));
    }

    public static TheoryData<string, string, string> OldSpellings => new()
    {
        { "bits = 10", $"{Coils} = 10", Coils },
        { "read_only_bits = 20", $"{Discrete} = 20", Discrete },
        { "words = 30", $"{Holding} = 30", Holding },
        { "read_only_words = 40", $"{Input} = 40", Input },
        { "kind = \"words\"\noffset = 0\ntype = \"real\"", $"kind = \"{Holding}\"\noffset = 0\ntype = \"real\"", $"variable.kind value \"{Holding}\" is invalid (bits|read_only_bits|words|read_only_words)" },
        { "kind = \"read_only_bits\"", $"kind = \"{Discrete}\"", $"variable.kind value \"{Discrete}\" is invalid" },
        { "kind = \"words\"\noffset = 10", $"kind = \"{Holding}\"\noffset = 10", $"server.map kind value \"{Holding}\" is invalid (bits|read_only_bits|words|read_only_words)" },
        { "local_kind = \"bits\"", $"local_kind = \"{Coil}\"", $"poll.local_kind value \"{Coil}\" is invalid (bits|read_only_bits|words|read_only_words)" },
    };

    [Theory]
    [MemberData(nameof(OldSpellings))]
    public void ModbusVocabulary_IsRejected(string find, string replace, string why)
    {
        Assert.NotEqual(find, replace);
        var r = Load("old.toml", Base.Replace(find, replace));
        Assert.False(r.Ok, "expected a config error containing: " + why);
        Assert.Contains(why, Messages(r));
    }

    [Fact]
    public void Device_And_TableKind_UseNeutralNames()
    {
        var device = new Device("d", bits: 1, readOnlyBits: 2, words: 3, readOnlyWords: 4);
        Assert.Equal((1, 2, 3, 4), (device.Length(TableKind.Bits), device.Length(TableKind.ReadOnlyBits), device.Length(TableKind.Words), device.Length(TableKind.ReadOnlyWords)));
        Assert.Equal(["Bits", "ReadOnlyBits", "Words", "ReadOnlyWords"], Enum.GetNames<TableKind>());
        Assert.True(TableKind.ReadOnlyBits.IsBit());
        Assert.True(TableKind.ReadOnlyWords.IsWord());
    }
}
