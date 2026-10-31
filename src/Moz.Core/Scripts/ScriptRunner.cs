using Jint;
using Jint.Runtime;
using Microsoft.Extensions.Logging;
using Moz.Devices;
using Moz.Logging;

namespace Moz.Scripts;

/// <summary>
/// One periodic JavaScript script: an <see cref="IProtocolRunner"/> per
/// [[script]]. The engine lives for the whole run; the source is wrapped in
/// an IIFE each tick (const/let don't collide between runs, while assignments
/// to undeclared names persist on the global object). Script errors are
/// logged — never fatal, the loop keeps ticking until shutdown.
/// </summary>
public sealed class ScriptRunner : IProtocolRunner
{
    private readonly string _path;
    private readonly int _intervalMs;
    private readonly PlcApi _plc;
    private readonly ILogger _logger;

    public ScriptRunner(string name, string path, int intervalMs,
        IDeviceAccess device, VariableTable variables, ILogger logger)
    {
        Name = name;
        _path = path;
        _intervalMs = intervalMs;
        _plc = new PlcApi(device, variables);
        _logger = logger;
    }

    public string Name { get; }

    public async Task RunAsync(CancellationToken ct)
    {
        // a missing/unreadable script is a deployment error: fatal for this runner
        var source = File.ReadAllText(_path);

        using var engine = new Engine();
        Prepare(engine);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ExecuteTick(engine, source);
                await Task.Delay(_intervalMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown between ticks
        }
    }

    /// <summary>One tick. Exceptions are user script errors: logged, swallowed.</summary>
    internal void ExecuteTick(Engine engine, string source)
    {
        try
        {
            engine.Execute(Wrap(source));
        }
        catch (JavaScriptException ex)
        {
            ScriptLog.Error(_logger, Name, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ScriptLog.Error(_logger, Name, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test convenience: one tick with a fresh engine, source re-read from disk.</summary>
    internal Task ExecuteOnceAsync(CancellationToken ct)
    {
        var engine = new Engine();
        Prepare(engine);
        ExecuteTick(engine, File.ReadAllText(_path));
        return Task.CompletedTask;
    }

    /// <summary>const/let declarations stay inside the IIFE; globals persist.</summary>
    internal static string Wrap(string source) => "(() => {\n" + source + "\n})();";

    private void Prepare(Engine engine)
    {
        // PlcApi is engine-neutral; this thin adapter is where Jint knows about it
        engine.SetValue("plc", new ScriptPlc(engine, _plc));
        engine.SetValue("console", new ScriptConsole(_logger, Name));
    }
}

/// <summary>
/// Jint-facing view of <see cref="PlcApi" />. The one engine-specific rule:
/// LINT/ULINT values convert to standard JS BigInt (JsBigInt) so 64-bit
/// arithmetic is exact; everything else marshals natively.
/// </summary>
public sealed class ScriptPlc(Engine engine, PlcApi plc)
{
    public Jint.Native.JsValue readVariable(string name, int index = 0) =>
        ToJs(plc.readVariable(name, index));

    public void writeVariable(string name, object? value, int index = 0) =>
        plc.writeVariable(name, value, index);

    public string[] getVariables() => plc.getVariables();

    public bool readBit(string table, int address) => plc.readBit(table, address);

    public void writeBit(string table, int address, bool value) => plc.writeBit(table, address, value);

    public ushort readWord(string table, int address) => plc.readWord(table, address);

    public void writeWord(string table, int address, ushort value) => plc.writeWord(table, address, value);

    private Jint.Native.JsValue ToJs(object? value) => value switch
    {
        null => Jint.Native.JsValue.Null,
        System.Numerics.BigInteger b => new Jint.Native.JsBigInt(b),
        _ => Jint.Native.JsValue.FromObject(engine, value),
    };
}

/// <summary>The `console` object available to scripts.</summary>
public sealed class ScriptConsole(ILogger logger, string scriptName)
{
    public void log(params Jint.Native.JsValue[] args)
    {
        ScriptLog.Message(logger, scriptName, string.Join(' ', args.Select(a => a.ToString())));
    }
}