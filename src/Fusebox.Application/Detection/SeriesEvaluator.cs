namespace Fusebox.Application.Detection;

/// <summary>One signal's verdict, with what it was based on.</summary>
public sealed record SignalVerdict(Signal Signal, Observation Observation, Verdict Verdict);

/// <summary>
/// Looks at one series' most recent minutes and asks the engine about every signal. Pure, like the engine.
/// </summary>
public sealed class SeriesEvaluator(DetectionOptions options)
{
    private static readonly Signal[] AllSignals = Enum.GetValues<Signal>();

    private readonly AnomalyEngine _engine = new(options);

    /// <summary>All signals the detector watches.</summary>
    public static IReadOnlyList<Signal> Signals => AllSignals;

    /// <summary>
    /// Learns what is normal for each signal of a series, from its history up to <paramref name="windowStart"/>.
    /// A signal with too little history has no baseline yet.
    /// </summary>
    public IReadOnlyDictionary<Signal, Baseline?> BuildBaselines(
        IReadOnlyDictionary<DateTimeOffset, MinuteStats> history,
        DateTimeOffset windowStart) =>
        AllSignals.ToDictionary(signal => signal, signal => BaselineBuilder.Build(signal, history, windowStart, options));

    /// <summary>
    /// Judges every signal of one series over the window [<paramref name="windowStart"/>, <paramref name="windowStart"/> + window).
    /// </summary>
    /// <param name="minutes">The series' minutes inside the window. A missing minute had no traffic.</param>
    /// <param name="baselines">What is normal, from <see cref="BuildBaselines"/>.</param>
    /// <param name="open">The series' anomalies that have not ended.</param>
    /// <param name="lastClosed">When each signal's most recent anomaly on this series ended, for the cooldown.</param>
    public IReadOnlyList<SignalVerdict> Evaluate(
        IReadOnlyDictionary<DateTimeOffset, MinuteStats> minutes,
        DateTimeOffset windowStart,
        IReadOnlyDictionary<Signal, Baseline?> baselines,
        IReadOnlyDictionary<Signal, OpenAnomaly> open,
        IReadOnlyDictionary<Signal, DateTimeOffset> lastClosed,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(minutes);
        ArgumentNullException.ThrowIfNull(baselines);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(lastClosed);

        var windowEnd = windowStart.AddMinutes(options.WindowMinutes);
        var requests = minutes.Values.Where(m => m.Bucket >= windowStart && m.Bucket < windowEnd).Sum(m => m.Requests);
        var verdicts = new List<SignalVerdict>(AllSignals.Length);

        foreach (var signal in AllSignals)
        {
            var observation = new Observation(
                CurrentValue(signal, minutes, windowStart, windowEnd),
                requests,
                baselines.GetValueOrDefault(signal));

            var verdict = _engine.Evaluate(
                signal,
                observation,
                open.GetValueOrDefault(signal),
                lastClosed.TryGetValue(signal, out var closedAt) ? closedAt : null,
                now);

            verdicts.Add(new SignalVerdict(signal, observation, verdict));
        }

        return verdicts;
    }

    // The median of the window's per-minute values, so one odd minute cannot start or stop an anomaly by itself.
    private double? CurrentValue(Signal signal, IReadOnlyDictionary<DateTimeOffset, MinuteStats> minutes, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var values = SignalExtractor.Values(signal, minutes, windowStart, windowEnd, zeroFillFrom: windowStart, options.MinForwardedPerMinute);

        return values.Count >= options.MinMinutesWithData ? RobustStatistics.Median(values) : null;
    }
}
