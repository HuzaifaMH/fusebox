using Fusebox.Application.Detection;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;

namespace Fusebox.Application.Tests;

/// <summary>What one minute of traffic looks like. Every request is forwarded.</summary>
internal sealed record Shape(
    double Requests,
    double ErrorRate = 0.01,
    double LatencyMedianMs = 400,
    double InputTokens = 20,
    double OutputTokens = 80,
    double CostPerRequestUsd = 0.002);

/// <summary>Builds minutes of synthetic traffic with a little noise, the same way every time for the same seed.</summary>
internal static class Traffic
{
    /// <summary>A Wednesday at midnight, UTC.</summary>
    public static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-03-04T00:00:00Z");

    public static readonly SeriesKey Acme = new(TenantId.Parse("acme"), "openai-chat");

    public static MinuteStats Minute(DateTimeOffset bucket, Shape shape, Random random, double noise = 0.15)
    {
        var requests = Math.Max(0, (long)Math.Round(shape.Requests * (1 + (((random.NextDouble() * 2) - 1) * noise))));

        long errors = 0;
        for (var i = 0; i < requests; i++)
        {
            if (random.NextDouble() < shape.ErrorRate)
            {
                errors++;
            }
        }

        double Jitter() => 1 + (((random.NextDouble() * 2) - 1) * 0.05);

        return new MinuteStats(
            bucket,
            requests,
            requests,
            errors,
            (long)Math.Round(requests * shape.InputTokens * Jitter()),
            (long)Math.Round(requests * shape.OutputTokens * Jitter()),
            (long)Math.Round(requests * shape.CostPerRequestUsd * Money.MicroDollarsPerDollar * Jitter()),
            LatencyCounts(requests, shape.LatencyMedianMs, random));
    }

    /// <summary>Spreads calls over the latency buckets as a log-normal distribution with the given median would.</summary>
    public static long[] LatencyCounts(long calls, double medianMs, Random random)
    {
        const double Sigma = 0.5;
        var bounds = LatencyBuckets.UpperBoundsMilliseconds;
        var counts = new long[LatencyBuckets.Count];
        var previous = 0.0;

        for (var i = 0; i < counts.Length; i++)
        {
            var cumulative = i < bounds.Count ? NormalCdf(Math.Log(bounds[i] / medianMs) / Sigma) : 1.0;
            counts[i] = (long)Math.Round(calls * (cumulative - previous) * (1 + (((random.NextDouble() * 2) - 1) * 0.05)));
            previous = cumulative;
        }

        return counts;
    }

    private static double NormalCdf(double x) => 0.5 * (1 + Erf(x / Math.Sqrt(2)));

    // Abramowitz and Stegun 7.1.26, accurate to about 1.5e-7.
    private static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1 / (1 + (0.3275911 * x));
        var polynomial = ((((((((1.061405429 * t) + -1.453152027) * t) + 1.421413741) * t) + -0.284496736) * t) + 0.254829592) * t;
        return sign * (1 - (polynomial * Math.Exp(-x * x)));
    }
}

/// <summary>A store that holds minutes in memory and keeps the anomalies it is told about.</summary>
internal sealed class FakeAnomalyStore : IAnomalyStore
{
    private readonly Dictionary<(SeriesKey Series, DateTimeOffset Bucket), MinuteStats> _minutes = [];
    private readonly List<OpenAnomaly> _open = [];
    private readonly Dictionary<(SeriesKey, Signal), DateTimeOffset> _closed = [];

    /// <summary>When false, another instance is pretending to hold the lock.</summary>
    public bool LockAvailable { get; set; } = true;

    public int HistoryLoads { get; private set; }

    public List<AnomalyChange> Changes { get; } = [];

    public IReadOnlyList<OpenAnomaly> Open => _open;

    public IReadOnlyDictionary<(SeriesKey, Signal), DateTimeOffset> Closed => _closed;

    public void Add(SeriesKey series, MinuteStats stats) => _minutes[(series, stats.Bucket)] = stats;

    public void SetOpen(OpenAnomaly anomaly) => _open.Add(anomaly);

    public async ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        if (!LockAvailable)
        {
            return false;
        }

        await work(cancellationToken);
        return true;
    }

    public ValueTask<IReadOnlyList<MinuteRow>> LoadMinutesAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MinuteRow>>([.. _minutes
            .Where(pair => pair.Key.Bucket >= windowStart && pair.Key.Bucket < windowEnd)
            .Select(pair => new MinuteRow(pair.Key.Series, pair.Value))]);

    public ValueTask<IReadOnlyList<MinuteRow>> LoadHistoryAsync(
        IReadOnlyCollection<SeriesKey> series,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges,
        CancellationToken cancellationToken)
    {
        HistoryLoads++;

        return ValueTask.FromResult<IReadOnlyList<MinuteRow>>([.. _minutes
            .Where(pair => series.Contains(pair.Key.Series) && ranges.Any(range => pair.Key.Bucket >= range.Start && pair.Key.Bucket < range.End))
            .Select(pair => new MinuteRow(pair.Key.Series, pair.Value))]);
    }

    public ValueTask<IReadOnlyList<OpenAnomaly>> LoadOpenAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<OpenAnomaly>>([.. _open]);

    public ValueTask<IReadOnlyDictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>> LoadRecentlyClosedAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyDictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>>(
            _closed.Where(pair => pair.Value >= since).ToDictionary(pair => (pair.Key.Item1, pair.Key.Item2), pair => pair.Value));

    public ValueTask SaveAsync(AnomalyChange change, CancellationToken cancellationToken)
    {
        Changes.Add(change);

        switch (change)
        {
            case AnomalyOpenedChange opened:
                _open.Add(opened.Anomaly);
                break;

            case AnomalyProgressedChange progressed:
                var index = _open.FindIndex(a => a.Id == progressed.Id);
                _open[index] = _open[index] with
                {
                    PeakValue = progressed.PeakValue,
                    PeakRatio = progressed.PeakRatio,
                    CalmEvaluations = progressed.CalmEvaluations,
                    LastSeenAt = progressed.LastSeenAt,
                };
                break;

            case AnomalyClosedChange closed:
                var anomaly = _open.Single(a => a.Id == closed.Id);
                _open.Remove(anomaly);
                _closed[(anomaly.Series, anomaly.Signal)] = closed.ClosedAt;
                break;
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingAnomalyListener : IAnomalyListener
{
    public List<(SeriesKey Series, Finding Finding, string Description, DateTimeOffset At)> Opened { get; } = [];

    public List<(OpenAnomaly Anomaly, DateTimeOffset At)> Closed { get; } = [];

    public void AnomalyOpened(SeriesKey series, Finding finding, string description, DateTimeOffset at) =>
        Opened.Add((series, finding, description, at));

    public void AnomalyClosed(OpenAnomaly anomaly, DateTimeOffset at) => Closed.Add((anomaly, at));
}
