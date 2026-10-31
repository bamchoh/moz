using System.Threading.Channels;

namespace Moz.Tests;

/// <summary>
/// In-memory byte pipe standing in for a serial link (test only): a write on
/// one end becomes readable on the other; a read blocks until data, peer
/// disposal (EOF), or cancellation. Serial's slow, unbuffered flavor is
/// approximated by handing the reader at most one byte per Read call.
/// </summary>
internal sealed class PipeStream : Stream
{
    private readonly Channel<byte> _inbound =
        Channel.CreateUnbounded<byte>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private PipeStream? _peer;

    public static (PipeStream A, PipeStream B) CreatePair()
    {
        var a = new PipeStream();
        var b = new PipeStream();
        a._peer = b;
        b._peer = a;
        return (a, b);
    }

    private PipeStream() { }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            var b = await _inbound.Reader.ReadAsync(ct);
            buffer.Span[0] = b;
            return 1;
        }
        catch (ChannelClosedException)
        {
            return 0; // peer closed
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            await _peer!._inbound.Writer.WriteAsync(buffer.Span[i], ct);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _peer?._inbound.Writer.TryComplete(); // peer sees EOF
        }
        base.Dispose(disposing);
    }
}