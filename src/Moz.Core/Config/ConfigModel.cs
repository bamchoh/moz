using Tomlyn.Serialization;

namespace Moz.Config;

/// <summary>Top-level config model (moz.toml). Docs/spec-config.md is normative.</summary>
public sealed class MozConfig
{
    [TomlPropertyName("log")]
    public LogConfig? Log { get; set; }

    [TomlPropertyName("device")]
    public List<DeviceConfig> Device { get; set; } = [];

    [TomlPropertyName("server")]
    public List<ServerConfig> Server { get; set; } = [];

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}

public sealed class LogConfig
{
    /// <summary>trace|debug|info|warn|error. Default: info.</summary>
    [TomlPropertyName("level")]
    public string? Level { get; set; }

    /// <summary>text|json. Default: text.</summary>
    [TomlPropertyName("format")]
    public string? Format { get; set; }

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

    /// <summary>Table sizes; absent keys default to 65536 (karakuri parity).</summary>
    [TomlPropertyName("coils")]
    public int? Coils { get; set; }

    [TomlPropertyName("discrete")]
    public int? Discrete { get; set; }

    [TomlPropertyName("holding")]
    public int? Holding { get; set; }

    [TomlPropertyName("input")]
    public int? Input { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
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

    /// <summary>Bind address. Default 127.0.0.1.</summary>
    [TomlPropertyName("bind")]
    public string? Bind { get; set; }

    /// <summary>Listen port. Default 50200. 0 = ephemeral (tests/CI).</summary>
    [TomlPropertyName("port")]
    public int? Port { get; set; }

    /// <summary>Unit ids served. Empty/absent = any unit id (karakuri parity).</summary>
    [TomlPropertyName("unit_ids")]
    public List<int>? UnitIds { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}