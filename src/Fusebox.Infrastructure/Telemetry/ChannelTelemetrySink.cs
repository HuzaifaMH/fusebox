using System.Threading.Channels;
using Fusebox.Application.Telemetry;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Telemetry;

/// <summary>
/// A bounded in-memory queue between requests and the writer. Handing a record over is a single non-blocking
/// operation, so recording can never add latency to a call, whatever the database is doing.
/// </summary>
internal sealed class ChannelTelemetrySink : ITelemetrySink
{
    private readonly Channel<RequestRecord> _channel;
    private readonly TelemetryMetrics _metrics;

    public ChannelTelemetrySink(IOptions<TelemetryOptions> options, TelemetryMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);

        _metrics = metrics;
        _channel = Channel.CreateBounded<RequestRecord>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            // With Wait, TryWrite returns false when full instead of discarding something, so the caller can count the drop.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        metrics.ObserveQueueLength(() => _channel.Reader.Count);
    }

    /// <summary>What the writer reads batches from.</summary>
    public ChannelReader<RequestRecord> Reader => _channel.Reader;

    public bool TryRecord(RequestRecord record)
    {
        if (_channel.Writer.TryWrite(record))
        {
            _metrics.Recorded.Add(1);
            return true;
        }

        _metrics.Dropped.Add(1, new KeyValuePair<string, object?>("reason", TelemetryMetrics.QueueFull));
        return false;
    }

    /// <summary>Stops accepting records. What is already queued can still be read, so a shutdown can flush it.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>Used when telemetry is switched off.</summary>
internal sealed class NullTelemetrySink : ITelemetrySink
{
    public static NullTelemetrySink Instance { get; } = new();

    public bool TryRecord(RequestRecord record) => true;
}
