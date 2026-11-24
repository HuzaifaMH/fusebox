using Fusebox.Application.Llm;
using Fusebox.Domain;

namespace Fusebox.Gateway.Middleware;

/// <summary>
/// Passes a streamed (server-sent events) response through event by event while reading the token usage the
/// vendor reports in it. Each event is forwarded as soon as it is complete, so callers still see tokens as
/// they arrive.
/// </summary>
/// <remarks>
/// When Fusebox asked the vendor to report usage on the caller's behalf, the usage event is not part of what the
/// caller expects, so it is read here and withheld.
/// </remarks>
internal sealed class SseUsageStream(Stream inner, ILlmProtocol protocol, bool hideUsageEvent, int maxPendingBytes) : Stream
{
    private static ReadOnlySpan<byte> DataField => "data:"u8;

    private static ReadOnlySpan<byte> UsageKey => "\"usage\""u8;

    private byte[] _pending = new byte[4096];
    private int _pendingLength;

    /// <summary>The usage the vendor reported, or null if none was seen.</summary>
    public TokenUsage? Usage { get; private set; }

    /// <summary>True if an event grew too large to inspect, so usage may have been missed.</summary>
    public bool Overflowed { get; private set; }

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

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Streamed responses are written asynchronously.");

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Append(buffer.Span);

        var consumed = 0;
        while (TryFindEventEnd(_pending.AsSpan(consumed, _pendingLength - consumed), out var length))
        {
            var eventBytes = _pending.AsMemory(consumed, length);

            if (!IsWithheld(eventBytes.Span))
            {
                await inner.WriteAsync(eventBytes, cancellationToken);
            }

            consumed += length;
        }

        Discard(consumed);

        // A single event this large is not a normal chunk, so stop holding it back.
        if (_pendingLength > maxPendingBytes)
        {
            Overflowed = true;
            await CompleteAsync(cancellationToken);
        }
    }

    /// <summary>Forwards anything still held back, such as a final event with no closing blank line.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingLength == 0)
        {
            return;
        }

        var remaining = _pendingLength;
        _pendingLength = 0;
        await inner.WriteAsync(_pending.AsMemory(0, remaining), cancellationToken);
    }

    // Reads usage from the event and returns whether to keep it from the caller.
    private bool IsWithheld(ReadOnlySpan<byte> sseEvent)
    {
        // Almost every event carries no usage, so rule those out without parsing anything.
        if (sseEvent.IndexOf(UsageKey) < 0 || IsNullUsage(sseEvent))
        {
            return false;
        }

        var withheld = false;

        foreach (var data in ReadDataLines(sseEvent))
        {
            if (protocol.TryReadStreamUsage(data, out var usage))
            {
                Usage = usage.Tokens;
                withheld |= hideUsageEvent && usage.IsUsageOnly;
            }
        }

        return withheld;
    }

    // Vendors that report usage in the stream send "usage": null on every other event.
    private static bool IsNullUsage(ReadOnlySpan<byte> sseEvent) =>
        sseEvent.IndexOf("\"usage\":null"u8) >= 0 || sseEvent.IndexOf("\"usage\": null"u8) >= 0;

    // The payload of each "data:" line of one event.
    private static List<byte[]> ReadDataLines(ReadOnlySpan<byte> sseEvent)
    {
        var payloads = new List<byte[]>();
        var start = 0;

        while (start < sseEvent.Length)
        {
            var newline = sseEvent[start..].IndexOf((byte)'\n');
            var end = newline < 0 ? sseEvent.Length : start + newline;
            var line = sseEvent[start..end];

            if (line.Length > 0 && line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }

            if (line.StartsWith(DataField))
            {
                var payload = line[DataField.Length..];

                // The format allows one optional space after the colon.
                payloads.Add((payload.Length > 0 && payload[0] == (byte)' ' ? payload[1..] : payload).ToArray());
            }

            start = end + 1;
        }

        return payloads;
    }

    private static bool TryFindEventEnd(ReadOnlySpan<byte> bytes, out int length)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n')
            {
                continue;
            }

            var afterBareBlankLine = i >= 1 && bytes[i - 1] == (byte)'\n';
            var afterCrlfBlankLine = i >= 3 && bytes[i - 1] == (byte)'\r' && bytes[i - 2] == (byte)'\n' && bytes[i - 3] == (byte)'\r';

            if (afterBareBlankLine || afterCrlfBlankLine)
            {
                length = i + 1;
                return true;
            }
        }

        length = 0;
        return false;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_pendingLength + data.Length > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingLength + data.Length));
        }

        data.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += data.Length;
    }

    private void Discard(int count)
    {
        if (count == 0)
        {
            return;
        }

        Buffer.BlockCopy(_pending, count, _pending, 0, _pendingLength - count);
        _pendingLength -= count;
    }
}
