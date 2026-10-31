using Moz.Devices;
using Tomlyn.Serialization;

namespace Moz.Config;

/// <summary>Top-level config model (moz.toml). Docs/spec-config.md is normative.</summary>
public sealed class MozConfig
{
    [TomlPropertyName("log")]
    public LogConfig? Log { get; set; }

    [TomlPropertyName("device")]
    public List<DeviceConfig> Device { get; set; } = [];

    [TomlPropertyName("variable")]
    public List<VariableConfig> Variable { get; set; } = [];

    [TomlPropertyName("server")]
    public List<ServerConfig> Server { get; set; } = [];

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}

public sealed class LogConfig
{
    /// <summary>trace|debug|info|warn|error. Default: info.</summary>
    [TomlPropertyName("level")]
    public string Level { get; set; } = "info";

    /// <summary>text|json. Default: text.</summary>
    [TomlPropertyName("format")]
    public string Format { get; set; } = "text";

    /// <summary>Optional JSON-lines log file. Empty/absent = console only.</summary>
    [TomlPropertyName("file")]
    public string? File { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}

/// <summary>
/// One simulated device = one independent memory space. Servers attach to a
/// device by name (a future variable overlay attaches here as well).
/// </summary>
public sealed class DeviceConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Table sizes; defaults are 65536 (karakuri parity).</summary>
    [TomlPropertyName("coils")]
    public int Coils { get; set; } = 65536;

    [TomlPropertyName("discrete")]
    public int Discrete { get; set; } = 65536;

    [TomlPropertyName("holding")]
    public int Holding { get; set; } = 65536;

    [TomlPropertyName("input")]
    public int Input { get; set; } = 65536;

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}

/// <summary>
/// A named typed overlay over one device's memory, declared at top level
/// ([[variable]]) and referencing the device by name. Top-level because
/// Tomlyn 2.x cannot bind nested [[device.variables]] table arrays.
/// </summary>
public sealed class VariableConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>References a device name; must resolve or the config is invalid.</summary>
    [TomlPropertyName("device")]
    public string Device { get; set; } = "";

    /// <summary>coil | discrete | holding | input.</summary>
    [TomlPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>0-based start index.</summary>
    [TomlPropertyName("offset")]
    public int Offset { get; set; }

    /// <summary>bool | int | uint | dint | udint | real.</summary>
    [TomlPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Number of elements (1 = scalar). Default 1.</summary>
    [TomlPropertyName("count")]
    public int Count { get; set; } = 1;

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }

    /// <summary>Parse after validation; throws on invalid strings (validated first).</summary>
    public TableKind ToTableKind() => Kind switch
    {
        "coil" => TableKind.Coil,
        "discrete" => TableKind.DiscreteInput,
        "holding" => TableKind.HoldingRegister,
        "input" => TableKind.InputRegister,
        _ => throw new FormatException($"invalid kind \"{Kind}\""),
    };

    /// <summary>Parse after validation; throws on invalid strings (validated first).</summary>
    public VariableType ToVariableType() => Type switch
    {
        "bool" => VariableType.Bool,
        "int" => VariableType.Int,
        "uint" => VariableType.Uint,
        "dint" => VariableType.Dint,
        "udint" => VariableType.Udint,
        "real" => VariableType.Real,
        _ => throw new FormatException($"invalid type \"{Type}\""),
    };
}

public sealed class ServerConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Phase 1: only "modbus-tcp". (modbus-rtu etc. arrive in later phases.)</summary>
    [TomlPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    /// <summary>References a device name; must resolve or the config is invalid.</summary>
    [TomlPropertyName("device")]
    public string Device { get; set; } = "";

    /// <summary>Bind address. Default 127.0.0.1 (normalized in Load).</summary>
    [TomlPropertyName("bind")]
    public string? Bind { get; set; }

    /// <summary>Listen port. Default 50200. 0 = ephemeral (tests/CI).</summary>
    [TomlPropertyName("port")]
    public int Port { get; set; } = 50200;

    /// <summary>Unit ids served. Empty/absent = any unit id (karakuri parity).</summary>
    [TomlPropertyName("unit_ids")]
    public List<int>? UnitIds { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}