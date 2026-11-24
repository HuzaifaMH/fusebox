namespace Fusebox.Application.Telemetry;

/// <summary>How wide a rollup bucket is.</summary>
public enum RollupGranularity
{
    Minute,
    Hour,
}

/// <summary>Port: where request records are kept and totalled.</summary>
public interface ITelemetryStore
{
    /// <summary>
    /// Saves a batch of records. Saving the same record twice is harmless, so a failed batch can be sent again.
    /// Throws if the batch could not be saved.
    /// </summary>
    ValueTask WriteAsync(IReadOnlyList<RequestRecord> batch, CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes the totals of every bucket that overlaps [<paramref name="windowStart"/>, <paramref name="windowEnd"/>) from the raw
    /// records. Replaces the buckets rather than adding to them, so running it again, or for a window that overlaps an
    /// earlier run, never counts a record twice and picks up late-arriving records.
    /// Returns false if another gateway instance is already doing it.
    /// </summary>
    ValueTask<bool> RollUpAsync(RollupGranularity granularity, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>Deletes raw records from days entirely before <paramref name="cutoff"/>. Returns how many days were dropped.</summary>
    ValueTask<int> DropRawBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
