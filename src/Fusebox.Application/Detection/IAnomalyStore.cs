namespace Fusebox.Application.Detection;

/// <summary>Port: the minute totals the detector reads, and the anomalies it keeps.</summary>
public interface IAnomalyStore
{
    /// <summary>
    /// Runs <paramref name="work"/> unless another gateway instance is already running detection, in which case it
    /// returns false without running it. This is what lets every instance run the detector while only one does the work.
    /// </summary>
    ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);

    /// <summary>The totals for every series in [<paramref name="windowStart"/>, <paramref name="windowEnd"/>), one row per series and minute.</summary>
    ValueTask<IReadOnlyList<MinuteRow>> LoadMinutesAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>
    /// The totals for the given series over each of the given time ranges, one row per series and minute.
    /// Used to learn baselines, which look at a few ranges rather than all of history.
    /// </summary>
    ValueTask<IReadOnlyList<MinuteRow>> LoadHistoryAsync(
        IReadOnlyCollection<SeriesKey> series,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges,
        CancellationToken cancellationToken);

    /// <summary>Every anomaly that has started and not ended.</summary>
    ValueTask<IReadOnlyList<OpenAnomaly>> LoadOpenAsync(CancellationToken cancellationToken);

    /// <summary>When each series' signal last had an anomaly end, for those that ended at or after <paramref name="since"/>.</summary>
    ValueTask<IReadOnlyDictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>> LoadRecentlyClosedAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Saves a change to an anomaly.</summary>
    ValueTask SaveAsync(AnomalyChange change, CancellationToken cancellationToken);
}
