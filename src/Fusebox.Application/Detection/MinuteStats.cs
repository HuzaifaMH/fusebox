using Fusebox.Application.Telemetry;

namespace Fusebox.Application.Detection;

/// <summary>What one tenant's calls on one route added up to in one minute, across all models.</summary>
/// <param name="Bucket">The start of the minute.</param>
/// <param name="Requests">Every request, whatever became of it.</param>
/// <param name="Forwarded">Requests that went to the vendor.</param>
/// <param name="Errors">Forwarded requests the vendor answered with an error status.</param>
/// <param name="InputTokens">Input tokens the vendor reported.</param>
/// <param name="OutputTokens">Output tokens the vendor reported.</param>
/// <param name="CostMicroUsd">Money charged, in micro-dollars.</param>
/// <param name="LatencyBuckets">How many forwarded calls fell in each latency bucket (see <see cref="LatencyBuckets"/>).</param>
/// <param name="Repeats">Forwarded requests that repeated one sent a moment before.</param>
public sealed record MinuteStats(
    DateTimeOffset Bucket,
    long Requests,
    long Forwarded,
    long Errors,
    long InputTokens,
    long OutputTokens,
    long CostMicroUsd,
    IReadOnlyList<long> LatencyBuckets,
    long Repeats = 0)
{
    /// <summary>Adds up several rows for the same minute, such as one per model.</summary>
    public static MinuteStats Merge(IEnumerable<MinuteStats> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        MinuteStats? merged = null;

        foreach (var row in rows)
        {
            merged = merged is null ? row : merged.Plus(row);
        }

        return merged ?? throw new ArgumentException("There is nothing to merge.", nameof(rows));
    }

    private MinuteStats Plus(MinuteStats other)
    {
        var buckets = new long[Math.Max(LatencyBuckets.Count, other.LatencyBuckets.Count)];

        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = (i < LatencyBuckets.Count ? LatencyBuckets[i] : 0) + (i < other.LatencyBuckets.Count ? other.LatencyBuckets[i] : 0);
        }

        return new MinuteStats(
            Bucket,
            Requests + other.Requests,
            Forwarded + other.Forwarded,
            Errors + other.Errors,
            InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens,
            CostMicroUsd + other.CostMicroUsd,
            buckets,
            Repeats + other.Repeats);
    }
}

/// <summary>A minute of one series, as read from storage.</summary>
public sealed record MinuteRow(SeriesKey Series, MinuteStats Stats);
