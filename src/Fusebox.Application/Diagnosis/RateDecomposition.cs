namespace Fusebox.Application.Diagnosis;

/// <summary>The numerator and denominator of a ratio, such as cost over tokens, for one segment such as a model.</summary>
public readonly record struct SegmentRate(double Numerator, double Denominator);

/// <summary>How much of a change in an overall ratio one segment is responsible for.</summary>
/// <param name="Key">The segment, such as a model name.</param>
/// <param name="RateEffect">The part from the segment's own ratio changing, at the average weight of the segment.</param>
/// <param name="MixEffect">The part from the segment's weight changing, at the average ratio of the segment.</param>
public sealed record SegmentEffect(string Key, double RateEffect, double MixEffect)
{
    public double Total => RateEffect + MixEffect;
}

/// <summary>An overall ratio before and after, and what each segment contributed to the change.</summary>
public sealed record RateChange(double Before, double After, IReadOnlyList<SegmentEffect> Segments)
{
    public double Delta => After - Before;

    public double RateEffect => Segments.Sum(s => s.RateEffect);

    public double MixEffect => Segments.Sum(s => s.MixEffect);

    /// <summary>The part of the change the segments do not account for, for example cost recorded where tokens were not.</summary>
    public double Unexplained => Delta - RateEffect - MixEffect;
}

public static class RateDecomposition
{
    /// <summary>
    /// Splits a change in an overall ratio (for example blended cost per token) into what each segment's own ratio did
    /// and what a shift in weight between segments did (for example traffic moving to a pricier model).
    /// The two parts use average weights and ratios, so together they add up exactly to the change, with no leftover.
    /// Returns null if the ratio cannot be computed in either period.
    /// </summary>
    public static RateChange? Decompose(
        IReadOnlyDictionary<string, SegmentRate> before,
        IReadOnlyDictionary<string, SegmentRate> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var denominatorBefore = before.Values.Sum(s => s.Denominator);
        var denominatorAfter = after.Values.Sum(s => s.Denominator);

        if (denominatorBefore <= 0 || denominatorAfter <= 0)
        {
            return null;
        }

        var overallBefore = before.Values.Sum(s => s.Numerator) / denominatorBefore;
        var overallAfter = after.Values.Sum(s => s.Numerator) / denominatorAfter;
        var segments = new List<SegmentEffect>();

        foreach (var key in before.Keys.Union(after.Keys))
        {
            var hasBefore = before.TryGetValue(key, out var b) && b.Denominator > 0;
            var hasAfter = after.TryGetValue(key, out var a) && a.Denominator > 0;

            if (!hasBefore && !hasAfter)
            {
                continue;
            }

            var weightBefore = hasBefore ? b.Denominator / denominatorBefore : 0;
            var weightAfter = hasAfter ? a.Denominator / denominatorAfter : 0;

            // A segment present in only one period has nothing to compare its own ratio with, so it keeps the same ratio.
            var rateBefore = hasBefore ? b.Numerator / b.Denominator : a.Numerator / a.Denominator;
            var rateAfter = hasAfter ? a.Numerator / a.Denominator : rateBefore;

            segments.Add(new SegmentEffect(
                key,
                RateEffect: (weightBefore + weightAfter) / 2 * (rateAfter - rateBefore),
                MixEffect: (weightAfter - weightBefore) * ((rateBefore + rateAfter) / 2)));
        }

        return new RateChange(overallBefore, overallAfter, segments);
    }
}
