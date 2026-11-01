using System.Numerics;
using Moz.Devices;

namespace Moz.Scripts;

/// <summary>
/// The `plc` object exposed to scripts — engine-neutral on purpose (Jint
/// today; a different engine must not change this surface).
///
/// Variable access is type-aware: LINT/ULINT values surface as BigInt
/// (System.Numerics.BigInteger is the engine-agnostic equivalent), other
/// numerics as double (time/tod in milliseconds), bools as boolean, strings
/// as string, date/dt as DateTime (UTC; JS Date). Raw memory access uses the Modbus
/// table names; both short ("coil", "holding", ...) and long ("coils",
/// "holdingRegisters", ...) spellings are accepted.
/// </summary>
public sealed class PlcApi
{
    private readonly IDeviceAccess _device;
    private readonly VariableTable _variables;

    public PlcApi(IDeviceAccess device, VariableTable variables)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _variables = variables ?? throw new ArgumentNullException(nameof(variables));
    }

    // --- typed variables ----------------------------------------------------

    /// <summary>Read the value of a variable (number|boolean|BigInt).</summary>
    public object? readVariable(string name, int index = 0)
    {
        var v = _variables.Find(name);
        return v.Type switch
        {
            VariableType.Bool or VariableType.BoolWord => _variables.Read<bool>(name, index),
            VariableType.Int or VariableType.Dint => (double)_variables.Read<int>(name, index),
            VariableType.Uint or VariableType.Udint => (double)_variables.Read<uint>(name, index),
            VariableType.Real => _variables.Read<float>(name, index),
            VariableType.Lint => new BigInteger(_variables.Read<long>(name, index)),
            VariableType.Ulint => new BigInteger(_variables.Read<ulong>(name, index)),
            VariableType.String => _variables.Read<string>(name, index),
            VariableType.Time => (double)_variables.Read<int>(name, index),
            VariableType.Tod => (double)_variables.Read<uint>(name, index),
            VariableType.Date or VariableType.Dt => EpochSecondsToDateTime(_variables.Read<ulong>(name, index)),
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Type, null),
        };
    }

    /// <summary>Write a variable. Values arrive as bool / double / BigInteger; the
    /// target type decides how they are narrowed (and rejected when off-range).</summary>
    public void writeVariable(string name, object? value, int index = 0)
    {
        var v = _variables.Find(name);
        switch (v.Type)
        {
            case VariableType.Bool:
            case VariableType.BoolWord:
                _variables.Write(name, Require<bool>(value, v), index);
                break;
            case VariableType.Int:
            case VariableType.Dint:
                _variables.Write(name, (int)RequireDouble(value, v, int.MinValue, int.MaxValue), index);
                break;
            case VariableType.Uint:
            case VariableType.Udint:
                _variables.Write(name, (uint)RequireDouble(value, v, uint.MinValue, uint.MaxValue), index);
                break;
            case VariableType.Real:
                _variables.Write(name, (float)RequireDouble(value, v, null, null), index);
                break;
            case VariableType.Lint:
                _variables.Write(name, (long)RequireBigInteger(value, v, long.MinValue, long.MaxValue), index);
                break;
            case VariableType.Ulint:
                _variables.Write(name, (ulong)RequireBigInteger(value, v, ulong.MinValue, ulong.MaxValue), index);
                break;
            case VariableType.String:
                _variables.Write(name, Require<string>(value, v), index);
                break;
            case VariableType.Time:
                _variables.Write(name, (int)RequireDouble(value, v, int.MinValue, int.MaxValue), index);
                break;
            case VariableType.Tod:
                _variables.Write(name, (uint)RequireDouble(value, v, uint.MinValue, uint.MaxValue), index);
                break;
            case VariableType.Date:
                _variables.Write(name, RequireEpochSeconds(value, v) / 86400 * 86400, index); // UTC midnight
                break;
            case VariableType.Dt:
                _variables.Write(name, RequireEpochSeconds(value, v), index);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(v), v.Type, null);
        }
    }

    /// <summary>Names of all declared variables (= the OPC UA node list).</summary>
    public string[] getVariables() => _variables.Variables.Select(v => v.Name).ToArray();

    // --- raw memory -----------------------------------------------------------

    /// <summary>Read one bit: areas "coil"|"coils", "discrete"|"discreteInputs".</summary>
    public bool readBit(string table, int address) =>
        _device.ReadBit(ParseTable(table), address);

    /// <summary>Write one bit to a coil table.</summary>
    public void writeBit(string table, int address, bool value) =>
        _device.WriteBit(TableForWrite(ParseTable(table)), address, value);

    /// <summary>Read one 16-bit word: areas "holding"|"holdingRegisters", "input"|"inputRegisters".</summary>
    public ushort readWord(string table, int address)
    {
        Span<ushort> w = stackalloc ushort[1];
        _device.Read(ParseTable(table), address, w);
        return w[0];
    }

    /// <summary>Write one 16-bit word to a holding register.</summary>
    public void writeWord(string table, int address, ushort value) =>
        _device.Write(TableForWrite(ParseTable(table)), address, new ushort[] { value });

    // --- helpers -----------------------------------------------------------------

    private static TableKind ParseTable(string table) => table switch
    {
        "coil" or "coils" => TableKind.Coil,
        "discrete" or "discreteInputs" => TableKind.DiscreteInput,
        "holding" or "holdingRegisters" => TableKind.HoldingRegister,
        "input" or "inputRegisters" => TableKind.InputRegister,
        _ => throw new ArgumentException(
            $"unknown memory area \"{table}\" (coil|coils|discrete|discreteInputs|holding|holdingRegisters|input|inputRegisters)"),
    };

    private static TableKind TableForWrite(TableKind table) =>
        table == TableKind.Coil ? TableKind.Coil : TableKind.HoldingRegister;

    private static T Require<T>(object? value, Variable v)
    {
        if (value is T t)
        {
            return t;
        }
        throw new ArgumentException($"variable \"{v.Name}\" expects {typeof(T).Name}, got {value?.GetType().Name ?? "null"}");
    }

    private static double RequireDouble(object? value, Variable v, double? min, double? max)
    {
        double d = value switch
        {
            bool b => b ? 1 : 0,             // convenience: writeVariable("flag", 1)
            double x => x,
            int x => x,
            uint x => x,
            long x => x,
            ulong x => x,
            BigInteger big => (double)big,
            float x => x,
            _ => throw new ArgumentException($"variable \"{v.Name}\" expects a number, got {value?.GetType().Name ?? "null"}"),
        };
        if ((min.HasValue && d < min) || (max.HasValue && d > max))
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"variable \"{v.Name}\": {d} out of range ({min}..{max})");
        }
        if (v.Type == VariableType.Real)
        {
            return d; // narrowing to float precision happens in the encoder
        }
        if (d != Math.Floor(d))
        {
            throw new ArgumentException($"variable \"{v.Name}\" expects an integer value, got {d}");
        }
        return d;
    }

    private static BigInteger RequireBigInteger(object? value, Variable v, BigInteger min, BigInteger max)
    {
        // BigInteger target makes the integer conversions implicit and exact
        BigInteger b = value switch
        {
            BigInteger x => x,
            long x => x,
            int x => x,
            uint x => x,
            ulong x => x,
            double d => (BigInteger)EnsureIntegral(d, v),
            _ => throw new ArgumentException($"variable \"{v.Name}\" expects an integer (BigInt), got {value?.GetType().Name ?? "null"}"),
        };
        if (b < min || b > max)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"variable \"{v.Name}\": {b} out of range ({min}..{max})");
        }
        return b;
    }

    // --- time -----------------------------------------------------------------

    private static readonly DateTime MaxEpoch = DateTime.MaxValue;

    /// <summary>Unix seconds → UTC DateTime (a JS Date in the script); clamps past year 9999.</summary>
    private static DateTime EpochSecondsToDateTime(ulong seconds) =>
        seconds > (ulong)(MaxEpoch - DateTime.UnixEpoch).TotalSeconds
            ? MaxEpoch
            : DateTime.UnixEpoch.AddSeconds(seconds);

    /// <summary>A JS Date (DateTime) or a number of epoch milliseconds → whole Unix seconds (≥ 0).</summary>
    private static ulong RequireEpochSeconds(object? value, Variable v)
    {
        double seconds = value switch
        {
            DateTime d => (d.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds,
            DateTimeOffset o => (o.UtcDateTime - DateTime.UnixEpoch).TotalSeconds,
            double ms => ms / 1000.0,
            int ms => ms / 1000.0,
            long ms => ms / 1000.0,
            BigInteger ms => (double)ms / 1000.0,
            _ => throw new ArgumentException(
                $"variable \"{v.Name}\" expects a Date or epoch milliseconds, got {value?.GetType().Name ?? "null"}"),
        };
        if (double.IsNaN(seconds) || seconds < 0 || seconds > ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value),
                $"variable \"{v.Name}\": {seconds} seconds is out of range (0..)");
        }
        return (ulong)Math.Floor(seconds);
    }

    private static double EnsureIntegral(double d, Variable v)
    {
        if (d != Math.Floor(d))
        {
            throw new ArgumentException($"variable \"{v.Name}\" expects an integer value, got {d}");
        }
        return d;
    }
}