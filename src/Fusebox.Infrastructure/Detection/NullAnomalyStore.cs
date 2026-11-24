using Fusebox.Application.Detection;

namespace Fusebox.Infrastructure.Detection;

/// <summary>Used when detection is off, so nothing is read or kept.</summary>
internal sealed class NullAnomalyStore : IAnomalyStore
{
    public ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<IReadOnlyList<MinuteRow>> LoadMinutesAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MinuteRow>>([]);

    public ValueTask<IReadOnlyList<MinuteRow>> LoadHistoryAsync(
        IReadOnlyCollection<SeriesKey> series,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MinuteRow>>([]);

    public ValueTask<IReadOnlyList<OpenAnomaly>> LoadOpenAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<OpenAnomaly>>([]);

    public ValueTask<IReadOnlyDictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>> LoadRecentlyClosedAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyDictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>>(new Dictionary<(SeriesKey, Signal), DateTimeOffset>());

    public ValueTask SaveAsync(AnomalyChange change, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
