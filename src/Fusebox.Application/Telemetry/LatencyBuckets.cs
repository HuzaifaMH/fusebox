namespace Fusebox.Application.Telemetry;

/// <summary>
/// The latency histogram that rollups keep, so percentiles can be estimated from totals without the raw records.
/// A call falls in the first bucket whose upper bound it does not exceed; slower calls fall in the last, open-ended one.
/// </summary>
public static class LatencyBuckets
{
    private static readonly int[] UpperBounds = [50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000, 30_000];

    /// <summary>The upper bound of every bucket except the last, in milliseconds, in order.</summary>
    public static IReadOnlyList<int> UpperBoundsMilliseconds => UpperBounds;

    /// <summary>How many buckets there are: one per bound, plus the open-ended last one.</summary>
    public static int Count => UpperBounds.Length + 1;
}
