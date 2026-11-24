using System.Buffers;

namespace Fusebox.Gateway.Middleware;

/// <summary>
/// Writes through to <paramref name="inner"/> unchanged while keeping a bounded copy of what was written.
/// The client sees no difference. Fusebox uses the copy to read the vendor-reported token usage.
/// </summary>
internal sealed class CapturingStream(Stream inner, int maxBytes) : Stream
{
    private readonly ArrayBufferWriter<byte> _captured = new();

    /// <summary>The bytes written so far, up to the limit. Meaningless once <see cref="Truncated"/> is true.</summary>
    public ReadOnlyMemory<byte> Captured => _captured.WrittenMemory;

    /// <summary>True if more than the limit was written, so the copy is incomplete.</summary>
    public bool Truncated { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Capture(buffer);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    private void Capture(ReadOnlySpan<byte> data)
    {
        if (Truncated)
        {
            return;
        }

        if (_captured.WrittenCount + data.Length > maxBytes)
        {
            Truncated = true;
            return;
        }

        _captured.Write(data);
    }
}
