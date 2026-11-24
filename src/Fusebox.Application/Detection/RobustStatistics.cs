using Fusebox.Application.Telemetry;

namespace Fusebox.Application.Detection;

/// <summary>
/// Statistics that a few wild values cannot drag around, which is why they are used for baselines:
/// a spike in the sample must not raise the bar that the next spike is judged against.
/// </summary>
public static class RobustStatistics
{
    /// <summary>The middle value. For an even count, the average of the two in the middle.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            throw new ArgumentException("There are no values.", nameof(values));
        }

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// The median absolute deviation: the median of how far each value is from the median. A typical distance
    /// from normal that, unlike the standard deviation, ignores outliers.
    /// </summary>
    public static double MedianAbsoluteDeviation(IReadOnlyList<double> values)
    {
        var median = Median(values);
        return Median([.. values.Select(value => Math.Abs(value - median))]);
    }

    /// <summary>
    /// Estimates a percentile from latency histogram counts, assuming calls are spread evenly inside a bucket.
    /// Returns null if there are no calls. The open-ended last bucket reports its lower bound, since nothing says how slow it was.
    /// </summary>
    public static double? LatencyPercentile(IReadOnlyList<long> bucketCounts, double percentile)
    {
        ArgumentNullException.ThrowIfNull(bucketCounts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1);

        var bounds = LatencyBuckets.UpperBoundsMilliseconds;
        var total = bucketCounts.Sum();

        if (total == 0)
        {
            return null;
        }

        var target = percentile * total;
        long seen = 0;

        for (var i = 0; i < bucketCounts.Count; i++)
        {
            var inBucket = bucketCounts[i];

            if (inBucket == 0 || seen + inBucket < target)
            {
                seen += inBucket;
                continue;
            }

            var lower = i == 0 ? 0 : bounds[i - 1];

            if (i >= bounds.Count)
            {
                return lower;
            }

            return lower + ((bounds[i] - lower) * ((target - seen) / inBucket));
        }

        return bounds[^1];
    }
}
