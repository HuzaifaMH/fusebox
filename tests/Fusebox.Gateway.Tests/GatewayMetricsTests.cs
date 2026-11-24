using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Telemetry;
using Fusebox.Application.Upstream;
using Fusebox.Domain;
using Fusebox.Gateway.Observability;

namespace Fusebox.Gateway.Tests;

/// <summary>What the gateway reports to Prometheus, and that it stays low in labels.</summary>
public sealed class GatewayMetricsTests : IDisposable
{
    private readonly ScopedMeterFactory _factory = new();
    private readonly SwitchableStatus _status = new();
    private readonly UpstreamBreakers _breakers;
    private readonly GatewayMetrics _metrics;
    private readonly Collector _collector;

    public GatewayMetricsTests()
    {
        _breakers = new UpstreamBreakers(
            Microsoft.Extensions.Options.Options.Create(new UpstreamBreakerOptions()),
            TimeProvider.System,
            [],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpstreamBreakers>.Instance);
        _metrics = new GatewayMetrics(_factory, _status, _breakers);
        _collector = new Collector(_factory);
    }

    public void Dispose()
    {
        _collector.Dispose();
        _metrics.Dispose();
    }

    private static RequestRecord Record(
        RequestDisposition disposition = RequestDisposition.Forwarded,
        int status = 200,
        DenialReason? denial = null,
        long costMicroUsd = 50_000,
        double seconds = 0.4,
        string route = "openai-chat") =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-03-04T10:15:00Z"),
            TenantId.Parse("acme"),
            route,
            "some-model-the-caller-chose",
            disposition,
            denial,
            status,
            TimeSpan.FromSeconds(seconds),
            false,
            null,
            Money.FromMicroDollars(costMicroUsd),
            Money.FromMicroDollars(100_000));

    [Fact]
    public void A_served_request_is_counted_with_its_route_outcome_and_status_class()
    {
        _metrics.Record(Record());

        Assert.Equal(1, _collector.Sum("fusebox.requests", ("route", "openai-chat"), ("outcome", "forwarded"), ("status_class", "2xx")));
    }

    [Theory]
    [InlineData(200, "2xx")]
    [InlineData(302, "3xx")]
    [InlineData(404, "4xx")]
    [InlineData(429, "4xx")]
    [InlineData(503, "5xx")]
    [InlineData(999, "other")]
    public void Status_codes_are_grouped_into_classes(int status, string expected)
    {
        _metrics.Record(Record(status: status));

        Assert.Equal(1, _collector.Sum("fusebox.requests", ("status_class", expected)));
    }

    [Fact]
    public void A_refusal_is_counted_by_reason()
    {
        _metrics.Record(Record(RequestDisposition.Denied, 429, DenialReason.BudgetExceeded, costMicroUsd: 0));
        _metrics.Record(Record(RequestDisposition.Denied, 429, DenialReason.BudgetExceeded, costMicroUsd: 0));
        _metrics.Record(Record(RequestDisposition.Denied, 402, DenialReason.UnknownModel, costMicroUsd: 0));

        Assert.Equal(2, _collector.Sum("fusebox.requests.refused", ("reason", "BudgetExceeded")));
        Assert.Equal(1, _collector.Sum("fusebox.requests.refused", ("reason", "UnknownModel")));
    }

    [Fact]
    public void Money_charged_is_added_up_and_a_free_call_adds_nothing()
    {
        _metrics.Record(Record(costMicroUsd: 50_000));
        _metrics.Record(Record(costMicroUsd: 25_000));
        _metrics.Record(Record(RequestDisposition.Denied, 429, DenialReason.BudgetExceeded, costMicroUsd: 0));

        Assert.Equal(75_000, _collector.Sum("fusebox.cost"));
    }

    [Fact]
    public void Duration_is_recorded_in_seconds()
    {
        _metrics.Record(Record(seconds: 1.5));

        Assert.Equal(1.5, Assert.Single(_collector.Durations), precision: 6);
    }

    [Fact]
    public void Series_do_not_multiply_with_tenants_or_models()
    {
        for (var i = 0; i < 50; i++)
        {
            _metrics.Record(Record() with { TenantId = TenantId.Parse($"tenant-{i}"), Model = $"model-{i}" });
        }

        Assert.Equal(1, _collector.SeriesCount("fusebox.requests"));
        Assert.DoesNotContain(_collector.TagKeys, key => key is "tenant" or "model" or "tenant_id");
    }

    [Fact]
    public void The_degraded_gauge_follows_the_enforcement_state()
    {
        Assert.Equal(0, _collector.ObserveGauge("fusebox.enforcement.degraded"));

        _status.IsDegraded = true;
        Assert.Equal(1, _collector.ObserveGauge("fusebox.enforcement.degraded"));
    }

    private sealed class SwitchableStatus : IEnforcementStatus
    {
        public bool IsDegraded { get; set; }
    }

    private sealed class ScopedMeterFactory : IMeterFactory
    {
        public Meter Create(MeterOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Scope = this;
            return new Meter(options);
        }

        public void Dispose()
        {
        }
    }

    private sealed class Collector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<(string Name, long Value, Dictionary<string, string?> Tags)> _longs = [];
        private readonly ConcurrentBag<double> _doubles = [];
        private readonly ConcurrentBag<string> _tagKeys = [];
        private readonly ConcurrentDictionary<string, int> _gauges = new();

        public Collector(ScopedMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _longs.Add((instrument.Name, value, ToDictionary(tags))));
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                _doubles.Add(value);
                ToDictionary(tags);
            });
            _listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => _gauges[instrument.Name] = value);
            _listener.Start();
        }

        public IEnumerable<double> Durations => _doubles;

        public IEnumerable<string> TagKeys => _tagKeys;

        public long Sum(string name, params (string Key, string Value)[] where) =>
            _longs.Where(m => m.Name == name && where.All(w => m.Tags.TryGetValue(w.Key, out var v) && v == w.Value)).Sum(m => m.Value);

        public int SeriesCount(string name) =>
            _longs.Where(m => m.Name == name).Select(m => string.Join('|', m.Tags.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"))).Distinct().Count();

        public int ObserveGauge(string name)
        {
            _listener.RecordObservableInstruments();
            return _gauges.GetValueOrDefault(name, -1);
        }

        public void Dispose() => _listener.Dispose();

        private Dictionary<string, string?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var result = new Dictionary<string, string?>();

            foreach (var tag in tags)
            {
                result[tag.Key] = tag.Value?.ToString();
                _tagKeys.Add(tag.Key);
            }

            return result;
        }
    }
}
