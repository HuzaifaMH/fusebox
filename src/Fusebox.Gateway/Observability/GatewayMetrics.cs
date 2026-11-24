using System.Diagnostics.Metrics;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Telemetry;
using Fusebox.Application.Upstream;

namespace Fusebox.Gateway.Observability;

/// <summary>
/// What the gateway reports about itself, for Prometheus. Labels are kept few and bounded (route, outcome, reason), because
/// a metric series is kept per label combination. Per-tenant and per-model detail lives in the database rollups.
/// </summary>
internal sealed class GatewayMetrics : IDisposable
{
    public const string MeterName = "Fusebox.Gateway";

    private readonly Meter _meter;

    public GatewayMetrics(IMeterFactory meterFactory, IEnforcementStatus enforcement, UpstreamBreakers upstreams)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(enforcement);
        ArgumentNullException.ThrowIfNull(upstreams);

        _meter = meterFactory.Create(MeterName);
        Requests = _meter.CreateCounter<long>("fusebox.requests", "{request}", "Requests handled, by route, outcome and status class.");
        Duration = _meter.CreateHistogram<double>(
            "fusebox.request.duration",
            "s",
            "Time from receiving a request to finishing its response. For a stream, the whole stream.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120] });
        Refused = _meter.CreateCounter<long>("fusebox.requests.refused", "{request}", "Requests Fusebox refused because of the budget, by reason.");
        Cost = _meter.CreateCounter<long>("fusebox.cost", "{micro_usd}", "Money charged against budgets, in millionths of a dollar, by route.");
        Repeated = _meter.CreateCounter<long>("fusebox.requests.repeated", "{request}", "Requests that repeat one sent a moment before, the signature of a retry loop, by route.");
        ShortCircuitedCalls = _meter.CreateCounter<long>(
            "fusebox.upstream.short_circuited",
            "{request}",
            "Calls refused at once because the vendor they go to was cut off for failing, by upstream.");
        _meter.CreateObservableGauge(
            "fusebox.upstream.circuit_open",
            () => upstreams.Snapshot().Select(pair => new Measurement<int>(pair.Value ? 1 : 0, new KeyValuePair<string, object?>("upstream", pair.Key))),
            "{state}",
            "1 while a vendor is cut off for failing, otherwise 0, by upstream.");
        _meter.CreateObservableGauge(
            "fusebox.enforcement.degraded",
            () => enforcement.IsDegraded ? 1 : 0,
            "{state}",
            "1 while the shared budget store is unreachable and a local limit is enforced, otherwise 0.");
    }

    public Counter<long> Requests { get; }

    public Histogram<double> Duration { get; }

    public Counter<long> Refused { get; }

    public Counter<long> Cost { get; }

    public Counter<long> Repeated { get; }

    public Counter<long> ShortCircuitedCalls { get; }

    /// <summary>Counts a call refused because its vendor is cut off.</summary>
    public void ShortCircuited(string upstream) => ShortCircuitedCalls.Add(1, new KeyValuePair<string, object?>("upstream", upstream));

    /// <summary>Counts one finished request.</summary>
    public void Record(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var outcome = record.Disposition.ToString().ToLowerInvariant();
        var route = new KeyValuePair<string, object?>("route", record.RouteId);

        Requests.Add(
            1,
            route,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("status_class", StatusClass(record.StatusCode)));
        Duration.Record(record.Duration.TotalSeconds, route, new KeyValuePair<string, object?>("outcome", outcome));

        if (record.IsRepeat)
        {
            Repeated.Add(1, route);
        }

        if (record.DenialReason is { } reason)
        {
            Refused.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));
        }

        if (record.Cost.MicroDollars > 0)
        {
            Cost.Add(record.Cost.MicroDollars, route);
        }
    }

    public void Dispose() => _meter.Dispose();

    private static string StatusClass(int status) => status switch
    {
        >= 200 and < 300 => "2xx",
        >= 300 and < 400 => "3xx",
        >= 400 and < 500 => "4xx",
        >= 500 and < 600 => "5xx",
        _ => "other",
    };
}
