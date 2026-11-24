namespace Fusebox.Application.Detection;

/// <summary>Turns a minute's totals into the value of a signal.</summary>
public static class SignalExtractor
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The signal's value for one minute, or null if the minute had too few forwarded calls to measure it
    /// (the minimum-volume gate). Volume and spend are always measurable.
    /// </summary>
    public static double? Value(Signal signal, MinuteStats minute, int minForwarded)
    {
        ArgumentNullException.ThrowIfNull(minute);

        return signal switch
        {
            Signal.Volume => minute.Requests,
            Signal.Spend => minute.CostMicroUsd,
            _ when minute.Forwarded < Math.Max(minForwarded, 1) => null,
            Signal.ErrorRate => (double)minute.Errors / minute.Forwarded,
            Signal.LatencyP95 => RobustStatistics.LatencyPercentile(minute.LatencyBuckets, 0.95),
            Signal.InputTokensPerRequest => (double)minute.InputTokens / minute.Forwarded,
            Signal.OutputTokensPerRequest => (double)minute.OutputTokens / minute.Forwarded,
            Signal.RepeatRate => (double)minute.Repeats / minute.Forwarded,
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown signal."),
        };
    }

    /// <summary>
    /// The signal's value for each minute in [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>) that can be measured.
    /// A minute with no row for volume or spend had no traffic, which is a real zero, so it counts as 0 from
    /// <paramref name="zeroFillFrom"/> on, or from the first minute that has a row when that is null. Before that the
    /// series may not have existed yet.
    /// </summary>
    public static IReadOnlyList<double> Values(
        Signal signal,
        IReadOnlyDictionary<DateTimeOffset, MinuteStats> minutes,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd,
        DateTimeOffset? zeroFillFrom,
        int minForwarded)
    {
        ArgumentNullException.ThrowIfNull(minutes);

        var isCount = signal is Signal.Volume or Signal.Spend;
        var values = new List<double>();
        var fillFrom = zeroFillFrom ?? FirstRowIn(minutes, rangeStart, rangeEnd);

        for (var minute = rangeStart; minute < rangeEnd; minute += OneMinute)
        {
            if (minutes.TryGetValue(minute, out var stats))
            {
                if (Value(signal, stats, minForwarded) is { } value)
                {
                    values.Add(value);
                }
            }
            else if (isCount && fillFrom is { } from && minute >= from)
            {
                values.Add(0);
            }
        }

        return values;
    }

    private static DateTimeOffset? FirstRowIn(IReadOnlyDictionary<DateTimeOffset, MinuteStats> minutes, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        DateTimeOffset? first = null;

        for (var minute = rangeStart; minute < rangeEnd; minute += OneMinute)
        {
            if (minutes.ContainsKey(minute))
            {
                first = minute;
                break;
            }
        }

        return first;
    }
}
