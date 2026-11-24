namespace Fusebox.Application.Detection;

/// <summary>What a baseline was learned from.</summary>
public enum BaselineBasis
{
    /// <summary>The same hour of the week in earlier weeks, so a Monday morning is compared with earlier Monday mornings.</summary>
    HourOfWeek,

    /// <summary>The last few hours, used until there are enough earlier weeks.</summary>
    Recent,

    /// <summary>No history yet: a fixed threshold stands in for a baseline.</summary>
    Fixed,
}

/// <summary>What is normal for one signal on one series.</summary>
/// <param name="Median">The typical per-minute value.</param>
/// <param name="Mad">The median absolute deviation: how much it typically varies.</param>
/// <param name="Samples">How many minutes it was learned from.</param>
/// <param name="Basis">Where those minutes came from.</param>
public sealed record Baseline(double Median, double Mad, int Samples, BaselineBasis Basis);

public static class BaselineBuilder
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>
    /// Learns what is normal for a signal just before <paramref name="windowStart"/>. Prefers the same hour of the week in
    /// earlier weeks, falls back to the last few hours, and returns null if there is not enough history for either.
    /// </summary>
    public static Baseline? Build(
        Signal signal,
        IReadOnlyDictionary<DateTimeOffset, MinuteStats> minutes,
        DateTimeOffset windowStart,
        DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        ArgumentNullException.ThrowIfNull(options);

        var hourStart = SeasonalHourStart(windowStart, options);
        var seasonal = new List<double>();

        for (var week = 1; week <= options.SeasonalWeeks; week++)
        {
            var start = hourStart.AddDays(-7 * week);
            seasonal.AddRange(SignalExtractor.Values(signal, minutes, start, start + OneHour, zeroFillFrom: null, options.MinForwardedPerMinute));
        }

        if (seasonal.Count >= options.MinSeasonalSamples)
        {
            return Summarize(seasonal, BaselineBasis.HourOfWeek);
        }

        var recent = SignalExtractor.Values(signal, minutes, windowStart - options.RecentBaseline, windowStart, zeroFillFrom: null, options.MinForwardedPerMinute);

        return recent.Count >= options.MinRecentSamples ? Summarize(recent, BaselineBasis.Recent) : null;
    }

    /// <summary>
    /// The hour of the week the window belongs to, as the start of that hour. Taken from the window's newest minute, so
    /// when the window straddles an hour boundary it is compared with the hour that traffic is now in, not the one it just left.
    /// </summary>
    public static DateTimeOffset SeasonalHourStart(DateTimeOffset windowStart, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var newest = windowStart.AddMinutes(options.WindowMinutes - 1).UtcDateTime;
        return new DateTimeOffset(newest.Date.AddHours(newest.Hour), TimeSpan.Zero);
    }

    private static Baseline Summarize(IReadOnlyList<double> samples, BaselineBasis basis) =>
        new(RobustStatistics.Median(samples), RobustStatistics.MedianAbsoluteDeviation(samples), samples.Count, basis);
}
