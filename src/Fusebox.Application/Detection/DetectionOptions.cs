namespace Fusebox.Application.Detection;

/// <summary>
/// How sensitive anomaly detection is. Every noise control is a named setting, so a false alarm can be traced to
/// the gate that should have stopped it, and tuned.
/// </summary>
public sealed class DetectionOptions
{
    public const string SectionName = "Fusebox:Detection";

    /// <summary>Turns the detector on. Off by default, because it needs per-minute totals to read.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the detector looks for new anomalies and for ones that have calmed down.</summary>
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far behind the present the newest minute examined is, so that its totals are final before it is judged.
    /// Totals are refreshed every rollup interval, so this must be longer than that; two minutes is safe for the default of one.
    /// </summary>
    public TimeSpan Lag { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How many recent minutes are examined together. The current value of a signal is their median.</summary>
    public int WindowMinutes { get; set; } = 5;

    /// <summary>A rate signal is only judged if at least this many of the window's minutes had enough calls to measure it.</summary>
    public int MinMinutesWithData { get; set; } = 3;

    /// <summary>
    /// The minimum-volume gate, per minute: a minute with fewer forwarded calls says nothing reliable about error rate,
    /// latency or tokens per call.
    /// </summary>
    public int MinForwardedPerMinute { get; set; } = 3;

    /// <summary>The minimum-volume gate for the window: with fewer requests in total, nothing is reported.</summary>
    public long MinRequestsInWindow { get; set; } = 20;

    /// <summary>An anomaly starts when a signal is at least this many times its baseline.</summary>
    public double FireRatio { get; set; } = 4;

    /// <summary>
    /// An anomaly can only end once the signal is back to at most this many times its baseline. Lower than
    /// <see cref="FireRatio"/> on purpose (hysteresis), so a signal hovering near the line does not flap.
    /// </summary>
    public double ClearRatio { get; set; } = 2;

    /// <summary>
    /// How far above normal the signal must be, in robust standard deviations. Guards noisy series, where a ratio alone
    /// is easily reached by chance.
    /// </summary>
    public double MinRobustZ { get; set; } = 4;

    /// <summary>How many evaluations in a row the signal must stay at or below <see cref="ClearRatio"/> before the anomaly ends.</summary>
    public int CalmEvaluationsToClear { get; set; } = 3;

    /// <summary>After an anomaly ends, how long before the same signal on the same series can start a new one.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How far back the "recent" baseline looks.</summary>
    public TimeSpan RecentBaseline { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How many earlier weeks the hour-of-week baseline draws on.</summary>
    public int SeasonalWeeks { get; set; } = 4;

    /// <summary>The hour-of-week baseline is used once it has this many minutes of samples (60 per week), else the recent one.</summary>
    public int MinSeasonalSamples { get; set; } = 120;

    /// <summary>
    /// The recent baseline needs this many minutes of samples. Until then only the fixed thresholds below apply,
    /// which is why detection works after about 20 minutes of traffic.
    /// </summary>
    public int MinRecentSamples { get; set; } = 20;

    /// <summary>How long a computed baseline is reused before the history is read again.</summary>
    public TimeSpan BaselineRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    public SignalThresholds Volume { get; set; } = new() { Floor = 1, MinIncrease = 10 };

    public SignalThresholds Spend { get; set; } = new() { Floor = 10_000, MinIncrease = 250_000 };

    public SignalThresholds ErrorRate { get; set; } = new() { Floor = 0.02, MinIncrease = 0.2, FixedThreshold = 0.5 };

    public SignalThresholds LatencyP95 { get; set; } = new() { Floor = 200, MinIncrease = 1_000, FixedThreshold = 10_000 };

    public SignalThresholds InputTokensPerRequest { get; set; } = new() { Floor = 50, MinIncrease = 500 };

    public SignalThresholds OutputTokensPerRequest { get; set; } = new() { Floor = 50, MinIncrease = 500 };

    /// <summary>No fixed limit: lots of identical calls can be normal (a fixed prompt, a health check), so only a jump from what is usual counts.</summary>
    public SignalThresholds RepeatRate { get; set; } = new() { Floor = 0.05, MinIncrease = 0.3 };

    public SignalThresholds For(Signal signal) => signal switch
    {
        Signal.Volume => Volume,
        Signal.Spend => Spend,
        Signal.ErrorRate => ErrorRate,
        Signal.LatencyP95 => LatencyP95,
        Signal.InputTokensPerRequest => InputTokensPerRequest,
        Signal.OutputTokensPerRequest => OutputTokensPerRequest,
        Signal.RepeatRate => RepeatRate,
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown signal."),
    };
}

/// <summary>The absolute limits for one signal, in the signal's own unit (see <see cref="Signal"/>).</summary>
public sealed class SignalThresholds
{
    /// <summary>
    /// The smallest baseline a ratio is measured against. Without it a baseline near zero would turn any trace of
    /// activity into a huge ratio.
    /// </summary>
    public double Floor { get; set; }

    /// <summary>How much the signal must exceed its baseline by, in absolute terms, so tiny numbers cannot alarm on ratio alone.</summary>
    public double MinIncrease { get; set; }

    /// <summary>
    /// Used while there is not yet enough history for a baseline: the signal alone at or above this value is an anomaly.
    /// Only for signals that have an absolute meaning. Null means no detection without a baseline.
    /// </summary>
    public double? FixedThreshold { get; set; }
}
