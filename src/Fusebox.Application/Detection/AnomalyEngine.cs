namespace Fusebox.Application.Detection;

/// <summary>What the detector sees for one signal on one series right now.</summary>
/// <param name="Current">The signal's current value, or null if it could not be measured.</param>
/// <param name="WindowRequests">How many requests the series had in the window, for the minimum-volume gate.</param>
/// <param name="Baseline">What is normal, or null if there is not yet enough history to say.</param>
public sealed record Observation(double? Current, long WindowRequests, Baseline? Baseline);

/// <summary>The facts about an anomaly at the moment it was found.</summary>
/// <param name="Signal">What jumped.</param>
/// <param name="Current">Its value now.</param>
/// <param name="Baseline">What is normal (or, for <see cref="BaselineBasis.Fixed"/>, the fixed threshold).</param>
/// <param name="Ratio">The value as a multiple of the baseline.</param>
/// <param name="RobustZ">How many robust standard deviations above normal it is. Zero for a fixed threshold.</param>
/// <param name="Basis">What the baseline was learned from.</param>
/// <param name="WindowRequests">Requests in the window.</param>
public sealed record Finding(
    Signal Signal,
    double Current,
    double Baseline,
    double Ratio,
    double RobustZ,
    BaselineBasis Basis,
    long WindowRequests);

/// <summary>An anomaly that has started and not yet ended.</summary>
public sealed record OpenAnomaly(
    Guid Id,
    SeriesKey Series,
    Signal Signal,
    DateTimeOffset OpenedAt,
    double Baseline,
    BaselineBasis Basis,
    double OpenedValue,
    double PeakValue,
    double PeakRatio,
    int CalmEvaluations,
    DateTimeOffset LastSeenAt);

/// <summary>What the engine decided about one signal on one series.</summary>
public abstract record Verdict
{
    private protected Verdict()
    {
    }
}

/// <summary>Nothing to report and nothing to change.</summary>
public sealed record NoChange : Verdict;

/// <summary>A new anomaly has started.</summary>
public sealed record Opened(Finding Finding) : Verdict;

/// <summary>The anomaly continues. It has not calmed down, so its calm count starts over.</summary>
public sealed record Continuing(double Value, double Ratio) : Verdict;

/// <summary>The signal is back to normal, but not yet for long enough to end the anomaly.</summary>
public sealed record Calming(int CalmEvaluations) : Verdict;

/// <summary>The anomaly has ended.</summary>
public sealed record Closed : Verdict;

/// <summary>
/// Decides whether a signal has jumped. Pure: the same inputs always give the same answer, with no clock, no storage
/// and no randomness, so every rule can be tested and an alert can always say exactly why it fired.
/// </summary>
/// <remarks>
/// A new anomaly needs every gate to pass: enough requests (minimum volume), the value at least <see cref="DetectionOptions.FireRatio"/>
/// times its baseline, an absolute increase of at least the signal's minimum, and a robust z-score of at least
/// <see cref="DetectionOptions.MinRobustZ"/>. An open anomaly ends only after <see cref="DetectionOptions.CalmEvaluationsToClear"/>
/// evaluations in a row at or below <see cref="DetectionOptions.ClearRatio"/>, and a closed one cannot start again
/// until the cooldown has passed. While it lasts, an anomaly is judged against the baseline it was opened with,
/// so a long anomaly cannot teach the detector that it is normal.
/// </remarks>
public sealed class AnomalyEngine(DetectionOptions options)
{
    // Turns a median absolute deviation into the equivalent of a standard deviation for normally distributed data.
    private const double MadToStandardDeviation = 1.4826;

    // The spread is never taken as less than this share of the baseline, so a perfectly steady series is not
    // treated as infinitely sensitive.
    private const double MinSpreadShare = 0.1;

    public Verdict Evaluate(
        Signal signal,
        Observation observation,
        OpenAnomaly? open,
        DateTimeOffset? lastClosedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return open is not null
            ? EvaluateOpen(signal, observation, open)
            : EvaluateNew(signal, observation, lastClosedAt, now);
    }

    private Verdict EvaluateNew(Signal signal, Observation observation, DateTimeOffset? lastClosedAt, DateTimeOffset now)
    {
        if (observation.Current is not { } current
            || observation.WindowRequests < options.MinRequestsInWindow
            || (lastClosedAt is { } closedAt && now - closedAt < options.Cooldown))
        {
            return new NoChange();
        }

        var limits = options.For(signal);

        if (observation.Baseline is not { } baseline)
        {
            return limits.FixedThreshold is { } threshold && current >= threshold
                ? new Opened(new Finding(signal, current, threshold, current / threshold, 0, BaselineBasis.Fixed, observation.WindowRequests))
                : new NoChange();
        }

        var denominator = Math.Max(baseline.Median, limits.Floor);
        var ratio = current / denominator;
        var increase = current - baseline.Median;
        var spread = Math.Max(baseline.Mad * MadToStandardDeviation, MinSpreadShare * denominator);
        var robustZ = increase / spread;

        return ratio >= options.FireRatio && increase >= limits.MinIncrease && robustZ >= options.MinRobustZ
            ? new Opened(new Finding(signal, current, baseline.Median, ratio, robustZ, baseline.Basis, observation.WindowRequests))
            : new NoChange();
    }

    private Verdict EvaluateOpen(Signal signal, Observation observation, OpenAnomaly open)
    {
        // No measurement means no traffic to judge, which counts as calm.
        var current = observation.Current ?? 0;
        var limits = options.For(signal);
        var isFixed = open.Basis == BaselineBasis.Fixed;

        var ratio = current / (isFixed ? open.Baseline : Math.Max(open.Baseline, limits.Floor));

        // A fixed threshold has no baseline to return to, so it ends when the signal is well under it.
        var clearAt = isFixed ? options.ClearRatio / options.FireRatio : options.ClearRatio;

        if (ratio > clearAt)
        {
            return new Continuing(current, ratio);
        }

        var calm = open.CalmEvaluations + 1;
        return calm >= options.CalmEvaluationsToClear ? new Closed() : new Calming(calm);
    }
}
