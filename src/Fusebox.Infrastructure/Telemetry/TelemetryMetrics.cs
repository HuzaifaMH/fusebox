using System.Diagnostics.Metrics;

namespace Fusebox.Infrastructure.Telemetry;

/// <summary>
/// How the telemetry pipeline itself is doing. A rising drop count means history is being lost, which is
/// otherwise silent because dropping is what keeps requests fast.
/// </summary>
internal sealed class TelemetryMetrics : IDisposable
{
    public const string MeterName = "Fusebox.Telemetry";

    public const string QueueFull = "queue_full";
    public const string WriteFailed = "write_failed";

    private readonly Meter _meter;

    public TelemetryMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);
        Recorded = _meter.CreateCounter<long>("fusebox.telemetry.recorded", "{record}", "Records accepted into the queue.");
        Written = _meter.CreateCounter<long>("fusebox.telemetry.written", "{record}", "Records saved to the database.");
        Dropped = _meter.CreateCounter<long>("fusebox.telemetry.dropped", "{record}", "Records lost, by reason.");
    }

    public Counter<long> Recorded { get; }

    public Counter<long> Written { get; }

    public Counter<long> Dropped { get; }

    /// <summary>Reports how many records are waiting to be saved.</summary>
    public void ObserveQueueLength(Func<int> length) =>
        _meter.CreateObservableGauge("fusebox.telemetry.queue.length", length, "{record}", "Records waiting to be saved.");

    public void Dispose() => _meter.Dispose();
}
