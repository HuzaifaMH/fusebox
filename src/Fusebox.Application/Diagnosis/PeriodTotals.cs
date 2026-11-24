using TelemetryLatency = Fusebox.Application.Telemetry.LatencyBuckets;

namespace Fusebox.Application.Diagnosis;

/// <summary>What one model's calls added up to over a period. An empty model name means the requests had none.</summary>
public sealed record ModelTotals(
    long Requests,
    long Forwarded,
    long Denied,
    long Rejected,
    long Unavailable,
    long Failed,
    long Errors,
    long InputTokens,
    long OutputTokens,
    long CostMicroUsd,
    IReadOnlyList<long> LatencyBuckets,
    long Repeats = 0)
{
    public long Tokens => InputTokens + OutputTokens;

    public ModelTotals Plus(ModelTotals other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var buckets = new long[Math.Max(LatencyBuckets.Count, other.LatencyBuckets.Count)];
        for (var i = 0; i < buckets.Length; i++)
        {
            buckets[i] = (i < LatencyBuckets.Count ? LatencyBuckets[i] : 0) + (i < other.LatencyBuckets.Count ? other.LatencyBuckets[i] : 0);
        }

        return new ModelTotals(
            Requests + other.Requests,
            Forwarded + other.Forwarded,
            Denied + other.Denied,
            Rejected + other.Rejected,
            Unavailable + other.Unavailable,
            Failed + other.Failed,
            Errors + other.Errors,
            InputTokens + other.InputTokens,
            OutputTokens + other.OutputTokens,
            CostMicroUsd + other.CostMicroUsd,
            buckets,
            Repeats + other.Repeats);
    }
}

/// <summary>One series' totals over a period of whole minutes, split by model.</summary>
/// <param name="Minutes">How many minutes of the period had any traffic recorded.</param>
/// <param name="PeriodMinutes">How long the period is, whether or not there was traffic in every minute.</param>
/// <param name="ByModel">Totals per model.</param>
public sealed record PeriodTotals(int Minutes, int PeriodMinutes, IReadOnlyDictionary<string, ModelTotals> ByModel)
{
    public static PeriodTotals Empty(int periodMinutes) => new(0, periodMinutes, new Dictionary<string, ModelTotals>());

    public long Requests => Sum(m => m.Requests);

    public long Forwarded => Sum(m => m.Forwarded);

    public long Denied => Sum(m => m.Denied);

    public long Rejected => Sum(m => m.Rejected);

    public long Errors => Sum(m => m.Errors);

    public long Repeats => Sum(m => m.Repeats);

    public long InputTokens => Sum(m => m.InputTokens);

    public long OutputTokens => Sum(m => m.OutputTokens);

    public long CostMicroUsd => Sum(m => m.CostMicroUsd);

    /// <summary>The latency histogram of all forwarded calls in the period.</summary>
    public IReadOnlyList<long> LatencyBuckets => Latency(ByModel.Values);

    public long Sum(Func<ModelTotals, long> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return ByModel.Values.Sum(selector);
    }

    public static IReadOnlyList<long> Latency(IEnumerable<ModelTotals> models)
    {
        var buckets = new long[TelemetryLatency.Count];

        foreach (var model in models)
        {
            for (var i = 0; i < model.LatencyBuckets.Count && i < buckets.Length; i++)
            {
                buckets[i] += model.LatencyBuckets[i];
            }
        }

        return buckets;
    }
}
