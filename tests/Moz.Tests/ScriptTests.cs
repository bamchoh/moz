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
}