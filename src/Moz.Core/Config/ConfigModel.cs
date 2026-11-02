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

    [TomlPropertyName("struct")]
    public List<StructConfig> Struct { get; set; } = [];

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
/// device by name (the [[variable]] overlay attaches by the same name).
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

    /// <summary>One of <see cref="VariableTypeNames.All"/>.</summary>
    [TomlPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Number of elements (1 = scalar). Default 1.</summary>
    [TomlPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>String byte length (1..250). Required for type string, forbidden otherwise.</summary>
    [TomlPropertyName("length")]
    public int? Length { get; set; }

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
    public VariableType ToVariableType() => VariableTypeNames.TryParse(Type, out var type)
        ? type
        : throw new FormatException($"invalid type \"{Type}\"");
}

/// <summary>
/// A user-defined struct type (phase 8): fields laid out sequentially on a
/// word table in declaration order. A [[variable]] whose type names a struct
/// expands into dotted leaf variables at load time (see VariableLayout).
/// </summary>
public sealed class StructConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    [TomlPropertyName("field")]
    public List<StructFieldConfig> Field { get; set; } = [];

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
}

/// <summary>One struct field: a scalar type or another struct (nesting allowed, no cycles).</summary>
public sealed class StructFieldConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>A scalar type name or a declared struct name.</summary>
    [TomlPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>Number of elements (1 = scalar). Default 1.</summary>
    [TomlPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>String byte length (1..250). Required for type string, forbidden otherwise.</summary>
    [TomlPropertyName("length")]
    public int? Length { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }
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
/// mirrors the answers into one local device ([[client.poll]]); writes
/// local changes back to the remote ([[client.push]]).
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

    [TomlPropertyName("push")]
    public List<PushConfig> Push { get; set; } = [];

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

    /// <summary>Default local_kind for a read fc; null for an invalid fc.</summary>
    public static string? DefaultLocalKind(int fc) => fc switch
    {
        1 => "coil",
        2 => "discrete",
        3 => "holding",
        4 => "input",
        _ => null,
    };

    /// <summary>The local table derived from fc (validation ensures kind consistency).</summary>
    public TableKind EffectiveLocalTable() =>
        LocalKinds.ToTable(LocalKind ?? DefaultLocalKind(Fc) ?? throw new FormatException($"invalid fc {Fc}"));
}

/// <summary>One [[client.push]] line: local cells written to the remote on change.</summary>
public sealed class PushConfig
{
    /// <summary>5|6|15|16 (write FCs only). Default 16 (write multiple registers).</summary>
    [TomlPropertyName("fc")]
    public int Fc { get; set; } = 16;

    /// <summary>Local table: coil|discrete|holding|input. Default derived from fc.</summary>
    [TomlPropertyName("local_kind")]
    public string? LocalKind { get; set; }

    /// <summary>First address of the local table.</summary>
    [TomlPropertyName("local_offset")]
    public int LocalOffset { get; set; }

    /// <summary>Number of cells (bits or words). Default 1; fc 5/6 require 1.</summary>
    [TomlPropertyName("count")]
    public int Count { get; set; } = 1;

    /// <summary>First address on the remote slave.</summary>
    [TomlPropertyName("remote_offset")]
    public int RemoteOffset { get; set; }

    [TomlExtensionData]
    public IDictionary<string, object?>? Extra { get; set; }

    /// <summary>Default local_kind for a write fc; null for an invalid fc.</summary>
    public static string? DefaultLocalKind(int fc) => fc switch
    {
        5 or 15 => "coil",
        6 or 16 => "holding",
        _ => null,
    };

    /// <summary>The local table derived from fc (validation ensures kind consistency).</summary>
    public TableKind EffectiveLocalTable() =>
        LocalKinds.ToTable(LocalKind ?? DefaultLocalKind(Fc) ?? throw new FormatException($"invalid fc {Fc}"));
}

/// <summary>local_kind spelling shared by poll and push.</summary>
public static class LocalKinds
{
    public static bool TryParse(string kind, out TableKind table)
    {
        switch (kind)
        {
            case "coil": table = TableKind.Coil; return true;
            case "discrete": table = TableKind.DiscreteInput; return true;
            case "holding": table = TableKind.HoldingRegister; return true;
            case "input": table = TableKind.InputRegister; return true;
            default: table = default; return false;
        }
    }

    public static TableKind ToTable(string kind) =>
        TryParse(kind, out var table) ? table : throw new FormatException($"invalid local_kind \"{kind}\"");
}

public sealed class ServerConfig
{
    [TomlPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>"modbus-tcp" | "modbus-rtu" | "opcua" (validated by ConfigLoader).</summary>
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