using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Detection;

/// <summary>
/// One round of anomaly detection: reads the latest minutes, judges every series and signal, and saves and announces
/// what changed. Scheduling is somebody else's job, so a round can be replayed against recorded or synthetic traffic.
/// </summary>
/// <remarks>
/// Anomalies live in the store, not in this object, so a restart or a hand-over between gateway instances loses nothing.
/// Baselines are learned from history and kept for a while, since reading weeks of history every minute would be wasteful.
/// </remarks>
public sealed partial class AnomalyDetector
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private readonly IAnomalyStore _store;
    private readonly IAnomalyListener[] _listeners;
    private readonly DetectionOptions _options;
    private readonly SeriesEvaluator _evaluator;
    private readonly ILogger<AnomalyDetector> _logger;

    // Only the instance that holds the detection lock touches this, and rounds do not overlap.
    private readonly Dictionary<SeriesKey, CachedBaselines> _baselines = [];

    public AnomalyDetector(
        IAnomalyStore store,
        IEnumerable<IAnomalyListener> listeners,
        IOptions<DetectionOptions> options,
        ILogger<AnomalyDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _listeners = [.. listeners];
        _options = options.Value;
        _evaluator = new SeriesEvaluator(_options);
        _logger = logger;
    }

    /// <summary>Runs one round as of <paramref name="now"/>. Does nothing if another gateway instance is already running one.</summary>
    public async ValueTask RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Judge whole minutes only, and only ones old enough that their totals are complete.
        var windowEnd = Truncate(now - _options.Lag);
        var windowStart = windowEnd - (_options.WindowMinutes * OneMinute);

        var ran = await _store.TryRunExclusivelyAsync(
            token => EvaluateAsync(now, windowStart, windowEnd, token),
            cancellationToken);

        if (!ran)
        {
            LogSkipped();
        }
    }

    private async Task EvaluateAsync(DateTimeOffset now, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        var rows = await _store.LoadMinutesAsync(windowStart, windowEnd, cancellationToken);
        var open = (await _store.LoadOpenAsync(cancellationToken)).ToLookup(a => a.Series);
        var recentlyClosed = await _store.LoadRecentlyClosedAsync(now - _options.Cooldown, cancellationToken);

        var windows = rows.GroupBy(row => row.Series).ToDictionary(
            group => group.Key,
            group => (IReadOnlyDictionary<DateTimeOffset, MinuteStats>)MergeByMinute(group));

        // A series with an open anomaly is judged even if it has gone quiet, which is how it gets to end.
        var series = windows.Keys.Concat(open.Select(group => group.Key)).Distinct().ToList();

        await RefreshBaselinesAsync(series, windowStart, now, cancellationToken);

        foreach (var key in series)
        {
            var openBySignal = open[key].ToDictionary(a => a.Signal);
            var closedBySignal = recentlyClosed
                .Where(pair => pair.Key.Series == key)
                .ToDictionary(pair => pair.Key.Signal, pair => pair.Value);

            var verdicts = _evaluator.Evaluate(
                windows.GetValueOrDefault(key) ?? new Dictionary<DateTimeOffset, MinuteStats>(),
                windowStart,
                _baselines[key].Baselines,
                openBySignal,
                closedBySignal,
                now);

            foreach (var verdict in verdicts)
            {
                await ApplyAsync(key, verdict, openBySignal.GetValueOrDefault(verdict.Signal), now, cancellationToken);
            }
        }

        EvictQuietBaselines(series);
    }

    private async Task ApplyAsync(SeriesKey series, SignalVerdict verdict, OpenAnomaly? open, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (verdict.Verdict)
        {
            case Opened opened:
                var anomaly = new OpenAnomaly(
                    Guid.CreateVersion7(now),
                    series,
                    verdict.Signal,
                    now,
                    opened.Finding.Baseline,
                    opened.Finding.Basis,
                    opened.Finding.Current,
                    opened.Finding.Current,
                    opened.Finding.Ratio,
                    0,
                    now);

                await _store.SaveAsync(new AnomalyOpenedChange(anomaly), cancellationToken);

                var description = AnomalyDescription.Of(series, opened.Finding, _options);
                LogOpened(series, verdict.Signal, description);
                Notify(listener => listener.AnomalyOpened(series, opened.Finding, description, now));
                break;

            case Continuing continuing when open is not null:
                await _store.SaveAsync(
                    new AnomalyProgressedChange(
                        open.Id,
                        Math.Max(open.PeakValue, continuing.Value),
                        Math.Max(open.PeakRatio, continuing.Ratio),
                        0,
                        now),
                    cancellationToken);
                break;

            case Calming calming when open is not null:
                await _store.SaveAsync(
                    new AnomalyProgressedChange(open.Id, open.PeakValue, open.PeakRatio, calming.CalmEvaluations, now),
                    cancellationToken);
                break;

            case Closed when open is not null:
                await _store.SaveAsync(new AnomalyClosedChange(open.Id, now), cancellationToken);
                LogClosed(series, verdict.Signal, now - open.OpenedAt);
                Notify(listener => listener.AnomalyClosed(open, now));
                break;

            default:
                break;
        }
    }

    // Learns baselines for series that have none yet, or whose are stale or for another hour of the week.
    private async Task RefreshBaselinesAsync(IReadOnlyList<SeriesKey> series, DateTimeOffset windowStart, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hourStart = BaselineBuilder.SeasonalHourStart(windowStart, _options);

        var stale = series
            .Where(key => !_baselines.TryGetValue(key, out var cached)
                || cached.HourStart != hourStart
                || now - cached.BuiltAt >= _options.BaselineRefreshInterval)
            .ToList();

        if (stale.Count == 0)
        {
            return;
        }

        var ranges = new List<(DateTimeOffset Start, DateTimeOffset End)> { (windowStart - _options.RecentBaseline, windowStart) };
        for (var week = 1; week <= _options.SeasonalWeeks; week++)
        {
            ranges.Add((hourStart.AddDays(-7 * week), hourStart.AddDays(-7 * week) + OneHour));
        }

        var history = (await _store.LoadHistoryAsync(stale, ranges, cancellationToken))
            .GroupBy(row => row.Series)
            .ToDictionary(group => group.Key, group => (IReadOnlyDictionary<DateTimeOffset, MinuteStats>)MergeByMinute(group));

        foreach (var key in stale)
        {
            var minutes = history.GetValueOrDefault(key) ?? new Dictionary<DateTimeOffset, MinuteStats>();
            _baselines[key] = new CachedBaselines(now, hourStart, _evaluator.BuildBaselines(minutes, windowStart));
        }
    }

    // Stops remembering series that have gone quiet, so the cache does not grow without bound.
    private void EvictQuietBaselines(IReadOnlyList<SeriesKey> active)
    {
        var keep = active.ToHashSet();

        foreach (var key in _baselines.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            _baselines.Remove(key);
        }
    }

    // A series can have several rows for one minute if they were read in pieces. Add them up.
    private static Dictionary<DateTimeOffset, MinuteStats> MergeByMinute(IEnumerable<MinuteRow> rows) =>
        rows.GroupBy(row => row.Stats.Bucket).ToDictionary(group => group.Key, group => MinuteStats.Merge(group.Select(row => row.Stats)));

    private static DateTimeOffset Truncate(DateTimeOffset instant) =>
        new(instant.UtcDateTime.Ticks - (instant.UtcDateTime.Ticks % OneMinute.Ticks), TimeSpan.Zero);

#pragma warning disable CA1031 // A listener must never be able to break detection.
    private void Notify(Action<IAnomalyListener> call)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                call(listener);
            }
            catch (Exception exception)
            {
                LogListenerFailed(exception, listener.GetType().Name);
            }
        }
    }
#pragma warning restore CA1031

    private sealed record CachedBaselines(DateTimeOffset BuiltAt, DateTimeOffset HourStart, IReadOnlyDictionary<Signal, Baseline?> Baselines);

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "Anomaly detected on {Series} ({Signal}): {Description}")]
    private partial void LogOpened(SeriesKey series, Signal signal, string description);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "Anomaly on {Series} ({Signal}) has ended after {Duration}")]
    private partial void LogClosed(SeriesKey series, Signal signal, TimeSpan duration);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Debug, Message = "Skipped anomaly detection: another gateway instance is already running it")]
    private partial void LogSkipped();

    [LoggerMessage(EventId = 6004, Level = LogLevel.Error, Message = "The anomaly listener {Listener} failed")]
    private partial void LogListenerFailed(Exception exception, string listener);
}
