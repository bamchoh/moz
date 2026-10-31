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

    [TomlPropertyName("client")]
    public List<ClientConfig> Client { get; set; } = [];

    [TomlPropertyName("script")]
    public List<ScriptConfig> Script { get; set; } = [];

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
        "lint" => VariableType.Lint,
        "ulint" => VariableType.Ulint,
        _ => throw new FormatException($"invalid type \"{Type}\""),
    };
}

/// <summary>A periodic JavaScript script (phase 4). Path is relative to the
/// config file; existence is checked at start, not at validation time.
/// Exactly one device is targeted (its raw memory + typed variables).</summary>
public sealed class ScriptConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>References a device name; must resolve or the config is invalid.</summary>
    [TomlPropertyName("device")]
    public string Device { get; set; } = "";

    [TomlPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>Period in milliseconds (100..3600000). Default 1000.</summary>
    [TomlPropertyName("interval_ms")]
    public int IntervalMs { get; set; } = 1000;

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}

/// <summary>
/// moz as Modbus master (phase 6): polls a remote slave on an interval and
/// mirrors the answers into one local device (read-only mirroring; writes
/// back to the remote come in a later phase).
/// </summary>
public sealed class ClientConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>The local device that receives the mirrored cells.</summary>
    [TomlPropertyName("device")]
    public string Device { get; set; } = "";

    /// <summary>host:port of the remote slave.</summary>
    [TomlPropertyName("remote_addr")]
    public string RemoteAddr { get; set; } = "";

    /// <summary>Unit id sent to the remote. Default 1.</summary>
    [TomlPropertyName("remote_unit")]
    public int RemoteUnit { get; set; } = 1;

    /// <summary>Poll period in milliseconds (100..3600000). Default 1000.</summary>
    [TomlPropertyName("interval_ms")]
    public int IntervalMs { get; set; } = 1000;

    /// <summary>Per-request wait (10..600000). Default 5000.</summary>
    [TomlPropertyName("timeout_ms")]
    public int TimeoutMs { get; set; } = 5000;

    [TomlPropertyName("poll")]
    public List<PollConfig> Poll { get; set; } = [];

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}

public sealed class PollConfig
{
    /// <summary>1..4 (read FCs only). Default 3 (read holding registers).</summary>
    [TomlPropertyName("fc")]
    public int Fc { get; set; } = 3;

    /// <summary>First address on the remote slave.</summary>
    [TomlPropertyName("remote_offset")]
    public int RemoteOffset { get; set; }

    /// <summary>Number of cells (bits or words). Default 1.</summary>
    [TomlPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>Local table: coil|discrete|holding|input. Default derived from fc.</summary>
    [TomlPropertyName("local_kind")]
    public string? LocalKind { get; set; }

    /// <summary>First address of the local table.</summary>
    [TomlPropertyName("local_offset")]
    public int LocalOffset { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }

    /// <summary>The local table derived from fc (validation ensures kind consistency).</summary>
    public TableKind EffectiveLocalTable() => (LocalKind ?? Fc switch
    {
        1 => "coil",
        2 => "discrete",
        3 => "holding",
        4 => "input",
        _ => throw new FormatException($"invalid fc {Fc}"),
    }) switch
    {
        "coil" => TableKind.Coil,
        "discrete" => TableKind.DiscreteInput,
        "holding" => TableKind.HoldingRegister,
        "input" => TableKind.InputRegister,
        _ => throw new FormatException($"invalid local_kind \"{LocalKind}\""),
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

    /// <summary>Serial port name for serial protocols (REQUIRED for modbus-rtu).
    /// Windows "COM10", Linux "/dev/ttyUSB0". Ignored-and-rejected on non-serial protocols.</summary>
    [TomlPropertyName("serial_port")]
    public string? SerialPort { get; set; }

    /// <summary>Baud rate. Default 9600 (Modbus serial default).</summary>
    [TomlPropertyName("baud")]
    public int Baud { get; set; } = 9600;

    /// <summary>none | even | odd. Default even (Modbus serial default 8E1).</summary>
    [TomlPropertyName("parity")]
    public string Parity { get; set; } = "even";

    /// <summary>Data bits (7..8). Default 8.</summary>
    [TomlPropertyName("data_bits")]
    public int DataBits { get; set; } = 8;

    /// <summary>Stop bits (1..2). Default 1.</summary>
    [TomlPropertyName("stop_bits")]
    public int StopBits { get; set; } = 1;

    /// <summary>Inter-frame idle gap (ms) that ends an RTU frame; 3.5 chars are ~4ms at 9600.
    /// Default 50.</summary>
    [TomlPropertyName("idle_gap_ms")]
    public int IdleGapMs { get; set; } = 50;

    /// <summary>OPC UA endpoint URL (REQUIRED for opcua): "opc.tcp://127.0.0.1:4840".
    /// Ignored-and-rejected on non-opcua protocols. The bind/port keys do not apply.</summary>
    [TomlPropertyName("endpoint")]
    public string? Endpoint { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}