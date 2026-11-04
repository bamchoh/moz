using Moz.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Moz.Tests;

/// <summary>
/// LINT/ULINT variables: 64-bit integers over 4 words (big-endian, karakuri
/// parity). These exist precisely because script engines use double for
/// numbers — the typed overlay keeps 64 bits exact via long/ulong and
/// BigInt at the script boundary.
/// </summary>
public class LintTests
{
    private readonly Device _device = new("t", words: 100);

    private static Variable V(string name, int offset, VariableType type) =>
        new(name, TableKind.Words, offset, type, 1);

    [Fact]
    public void WordSize_Is4()
    {
        Assert.Equal(4, Variable.WordSize(VariableType.Lint));
        Assert.Equal(4, Variable.WordSize(VariableType.Ulint));
    }

    [Fact]
    public void Lint_RoundTrip_NegativeAndLarge()
    {
        var table = new VariableTable(_device, [V("offset32", 0, VariableType.Lint)], NullLogger.Instance);
        table.Write("offset32", -1L);
        Assert.Equal(-1L, table.Read<long>("offset32"));

        table.Write("offset32", long.MaxValue);
        var w = new ushort[4];
        _device.Read(TableKind.Words, 0, w);
        Assert.Equal((ushort)0x7FFF, w[0]); // big-endian: high word first
        Assert.Equal((ushort)0xFFFF, w[1]);
        Assert.Equal((ushort)0xFFFF, w[2]);
        Assert.Equal((ushort)0xFFFF, w[3]);
        Assert.Equal(long.MaxValue, table.Read<long>("offset32"));
    }

    [Fact]
    public void Ulint_RoundTrip_MaxValue()
    {
        var table = new VariableTable(_device, [V("serial", 4, VariableType.Ulint)], NullLogger.Instance);
        table.Write("serial", ulong.MaxValue);
        var w = new ushort[4];
        _device.Read(TableKind.Words, 4, w);
        Assert.Equal((ushort)0xFFFF, w[0]);
        Assert.Equal((ushort)0xFFFF, w[3]);
        Assert.Equal(ulong.MaxValue, table.Read<ulong>("serial"));
    }

    [Fact]
    public void LintMin_RoundTrip()
    {
        var table = new VariableTable(_device, [V("v", 8, VariableType.Lint)], NullLogger.Instance);
        table.Write("v", long.MinValue);
        Assert.Equal(long.MinValue, table.Read<long>("v"));
    }

    [Fact]
    public void Ulint_HostTypeIsUlongNotInt()
    {
        var table = new VariableTable(_device, [V("serial", 0, VariableType.Ulint)], NullLogger.Instance);
        Assert.Throws<ArgumentException>(() => table.Read<int>("serial"));
        Assert.Throws<ArgumentException>(() => table.Write("serial", 1234)); // int not accepted
    }
}