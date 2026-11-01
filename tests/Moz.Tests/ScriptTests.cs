using Moz.Devices;
using Moz.Scripts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moz.Tests;

/// <summary>
/// Scripting (phase 4): a Jint-powered periodic runner with the `plc` object.
/// Key promise: LINT/ULINT variables surface as standard JS BigInt — no
/// special *Big API names; +, -, comparisons work in 64-bit precision.
/// Errors inside a script are logged and the loop keeps running.
/// </summary>
public class ScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "moz-scripttests-" + Guid.NewGuid().ToString("N"));

    public ScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string WriteScript(string name, string code)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, code);
        return path;
    }

    private static (Device Device, VariableTable Vars) MakeDevice()
    {
        var device = new Device("t", coils: 100, discreteInputs: 100, holdingRegisters: 100, inputRegisters: 100);
        var vars = new VariableTable(device,
        [
            new Variable("temperature", TableKind.HoldingRegister, 0, VariableType.Real),
            new Variable("copy", TableKind.HoldingRegister, 2, VariableType.Real),
            new Variable("flag", TableKind.Coil, 0, VariableType.Bool),
            new Variable("serial", TableKind.HoldingRegister, 4, VariableType.Ulint),
            new Variable("label", TableKind.HoldingRegister, 8, VariableType.String, Length: 8),
            new Variable("timer", TableKind.HoldingRegister, 12, VariableType.Time),
            new Variable("tod", TableKind.HoldingRegister, 14, VariableType.Tod),
            new Variable("day", TableKind.HoldingRegister, 16, VariableType.Date),
            new Variable("stamp", TableKind.HoldingRegister, 20, VariableType.Dt),
        ], NullLogger.Instance);
        return (device, vars);
    }

    private ScriptRunner MakeRunner(string code, VariableTable vars,
        CapturingProvider? provider = null, int intervalMs = 1000, string path = "s.js")
    {
        var scriptPath = WriteScript(path, code);
        var logger = provider is null
            ? NullLogger.Instance
            : provider.CreateLogger("test");
        return new ScriptRunner("s", scriptPath, intervalMs, vars.Device, vars, logger);
    }

    // --- typed variable access -------------------------------------------------

    [Fact]
    public async Task Script_WritesVariable_ThroughPlcObject()
    {
        var (device, vars) = MakeDevice();
        var runner = MakeRunner("plc.writeVariable('temperature', 12.5);", vars);
        await runner.ExecuteOnceAsync(default);

        var w = new ushort[2];
        device.Read(TableKind.HoldingRegister, 0, w);
        Assert.Equal((ushort)0x4148, w[0]); // IEEE754 12.5 big-endian
        Assert.Equal(0, w[1]);
    }

    [Fact]
    public async Task Script_ReadsVariable_AndCopiesIt()
    {
        var (_, vars) = MakeDevice();
        vars.Write("temperature", 7.25f);
        var runner = MakeRunner("plc.writeVariable('copy', plc.readVariable('temperature'));", vars);
        await runner.ExecuteOnceAsync(default);
        Assert.Equal(7.25f, vars.Read<float>("copy"));
    }

    [Fact]
    public async Task Script_BoolVariable_RoundTrip()
    {
        var (device, vars) = MakeDevice();
        var runner = MakeRunner("plc.writeVariable('flag', true);", vars);
        await runner.ExecuteOnceAsync(default);
        Assert.True(device.ReadBit(TableKind.Coil, 0));
        Assert.True(vars.Read<bool>("flag"));
    }

    // --- raw memory access ------------------------------------------------------

    [Fact]
    public async Task Script_RawWordAndBitAccess()
    {
        var (device, vars) = MakeDevice();
        device.Seed(TableKind.DiscreteInput, 3, new bool[] { true });
        var runner = MakeRunner(
            """
            plc.writeWord('holding', 5, 1234);
            plc.writeBit('coil', 2, true);
            if (plc.readBit('discreteInputs', 3) !== true) { throw new Error('discrete read failed'); }
            if (plc.readWord('holding', 5) !== 1234) { throw new Error('word read failed'); }
            """, vars);
        await runner.ExecuteOnceAsync(default);

        var w = new ushort[1];
        device.Read(TableKind.HoldingRegister, 5, w);
        Assert.Equal((ushort)1234, w[0]);
        Assert.True(device.ReadBit(TableKind.Coil, 2));
    }

    // --- BigInt: the 64-bit promise ----------------------------------------------

    [Fact]
    public async Task UlintVariable_BecomesBigInt_ArithmeticIsExact()
    {
        var (_, vars) = MakeDevice();
        vars.Write("serial", ulong.MaxValue - 1); // 0xFFFFFFFFFFFFFFFE
        const string code =
            """
            var x = plc.readVariable('serial');
            if (typeof BigInt === 'undefined' || typeof x !== 'bigint') { throw new Error('not a bigint: ' + typeof x); }
            plc.writeVariable('serial', x + 1n);   // 0xFFFF...FF — the whole point
            """;
        var runner = MakeRunner(code, vars);
        await runner.ExecuteOnceAsync(default);

        Assert.Equal(ulong.MaxValue, vars.Read<ulong>("serial"));
    }

    [Fact]
    public async Task Script_WritesUlint_FromBigIntLiteral()
    {
        var (_, vars) = MakeDevice();
        var runner = MakeRunner("plc.writeVariable('serial', 0xFFFFFFFFFFFFFFFFn);", vars);
        await runner.ExecuteOnceAsync(default);
        Assert.Equal(ulong.MaxValue, vars.Read<ulong>("serial"));
    }

    // --- error handling ------------------------------------------------------------

    [Fact]
    public async Task Script_Error_IsLogged_LoopSurvives()
    {
        var provider = new CapturingProvider();
        var (_, vars) = MakeDevice();
        var runner = MakeRunner("throw new Error('boom-42');", vars, provider);
        await runner.ExecuteOnceAsync(default);   // must throw nothing, log instead
        await runner.ExecuteOnceAsync(default);   // and a second run works
        var errs = provider.Entries.Where(e => e.Message.Contains("boom-42")).ToList();
        Assert.Equal(2, errs.Count);
        Assert.Contains(errs, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task ConsoleLog_GoesToLogger()
    {
        var provider = new CapturingProvider();
        var (_, vars) = MakeDevice();
        var runner = MakeRunner("console.log('hello', 42, true);", vars, provider);
        await runner.ExecuteOnceAsync(default);
        var line = provider.Entries.FirstOrDefault(e => e.Message.Contains("hello"));
        Assert.NotNull(line);
        Assert.Contains("42", line!.Message);
        Assert.Contains("true", line.Message);
    }

    // --- periodic loop ----------------------------------------------------------------

    [Fact]
    public async Task RunAsync_RepeatsUntilCancelled()
    {
        var (_, vars) = MakeDevice();
        var runner = MakeRunner("var n = (plc.readVariable('serial') || 0n) + 1n; plc.writeVariable('serial', n);",
            vars, intervalMs: 20);
        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync(cts.Token);
        await Task.Delay(120);
        await cts.CancelAsync();
        await task;

        var n = vars.Read<ulong>("serial");
        Assert.True(n >= 2, $"expected >= 2 ticks, got {n}");
    }

    // --- string and time types (phase 8) -----------------------------------------

    [Fact]
    public async Task Script_String_RoundTrip()
    {
        var (device, vars) = MakeDevice();
        var runner = MakeRunner(
            "plc.writeVariable('label', 'hi');\n" +
            "var s = plc.readVariable('label');\n" +
            "plc.writeVariable('copy', typeof s === 'string' ? s.length : -1);", vars);
        await runner.ExecuteOnceAsync(default);

        Assert.Equal("hi", vars.Read<string>("label"));
        var w = new ushort[1];
        device.Read(TableKind.HoldingRegister, 8, w);
        Assert.Equal((ushort)0x6869, w[0]);
        Assert.Equal(2f, vars.Read<float>("copy"));
    }

    [Fact]
    public async Task Script_Time_Tod_AreNumbersInMs()
    {
        var (_, vars) = MakeDevice();
        var runner = MakeRunner(
            "plc.writeVariable('timer', -1500);\n" +
            "plc.writeVariable('tod', 3600000);\n" +
            "var t = plc.readVariable('timer');\n" +
            "plc.writeVariable('copy', typeof t === 'number' ? t + plc.readVariable('tod') : -1);", vars);
        await runner.ExecuteOnceAsync(default);

        Assert.Equal(-1500, vars.Read<int>("timer"));
        Assert.Equal(3600000u, vars.Read<uint>("tod"));
        Assert.Equal(3598500f, vars.Read<float>("copy"));
    }

    [Fact]
    public async Task Script_Dt_IsJsDate_BothDirections()
    {
        var (_, vars) = MakeDevice();
        vars.Write("stamp", 1_793_525_400ul); // 2026-11-01T09:30:00Z
        var runner = MakeRunner(
            "var x = plc.readVariable('stamp');\n" +
            "plc.writeVariable('copy', x instanceof Date ? 1 : 0);\n" +
            "plc.writeVariable('tod', x.getTime() / 1000 - 1793000000);\n" +
            "plc.writeVariable('stamp', new Date(1793525460000));", vars);
        await runner.ExecuteOnceAsync(default);

        Assert.Equal(1f, vars.Read<float>("copy"));
        Assert.Equal(525400u, vars.Read<uint>("tod"));
        Assert.Equal(1_793_525_460ul, vars.Read<ulong>("stamp"));
    }

    [Fact]
    public async Task Script_Date_FloorsToMidnightUtc_And_AcceptsEpochMs()
    {
        var (_, vars) = MakeDevice();
        var runner = MakeRunner(
            "plc.writeVariable('day', new Date(Date.UTC(2026, 10, 1, 9, 30)));\n" +
            "plc.writeVariable('stamp', 1793525400000);", vars);
        await runner.ExecuteOnceAsync(default);

        Assert.Equal(1_793_491_200ul, vars.Read<ulong>("day"));
        Assert.Equal(1_793_525_400ul, vars.Read<ulong>("stamp"));
    }

    [Fact]
    public async Task Script_Dt_Negative_IsLoggedError_DeviceUntouched()
    {
        var (_, vars) = MakeDevice();
        vars.Write("stamp", 5ul);
        var provider = new CapturingProvider();
        var runner = MakeRunner("plc.writeVariable('stamp', -1);", vars, provider);
        await runner.ExecuteOnceAsync(default);

        Assert.Contains(provider.Entries, e => e.EventId == 2102);
        Assert.Equal(5ul, vars.Read<ulong>("stamp"));
    }

    [Fact]
    public async Task Script_String_TooLong_IsLoggedError()
    {
        var (_, vars) = MakeDevice();
        var provider = new CapturingProvider();
        var runner = MakeRunner("plc.writeVariable('label', '123456789');", vars, provider);
        await runner.ExecuteOnceAsync(default);

        Assert.Contains(provider.Entries, e => e.EventId == 2102);
        Assert.Equal("", vars.Read<string>("label"));
    }

    // --- struct leaves (phase 8) --------------------------------------------------------

    [Fact]
    public async Task Script_ReadsStructLeaf_ByDottedName()
    {
        var device = new Device("t", holdingRegisters: 100);
        var structs = new Dictionary<string, StructDef>
        {
            ["Motor"] = new("Motor", [new StructField("speed", "int"), new StructField("on", "bool"), new StructField("rpm", "real")]),
        };
        var leaves = VariableLayout.Expand("motor", TableKind.HoldingRegister, 10, "Motor", 1, 0, structs)
            .Append(new Variable("copy", TableKind.HoldingRegister, 0, VariableType.Real));
        var vars = new VariableTable(device, leaves, NullLogger.Instance);
        var runner = MakeRunner(
            "plc.writeVariable('motor.speed', 7);\n" +
            "plc.writeVariable('motor.on', true);\n" +
            "var names = plc.getVariables();\n" +
            "plc.writeVariable('copy', names.indexOf('motor.rpm') >= 0 && plc.readVariable('motor.on') === true ? 1 : 0);", vars);
        await runner.ExecuteOnceAsync(default);

        var w = new ushort[2];
        device.Read(TableKind.HoldingRegister, 10, w);
        Assert.Equal((ushort)7, w[0]);
        Assert.Equal((ushort)1, w[1]);
        Assert.Equal(1f, vars.Read<float>("copy"));
    }
}
