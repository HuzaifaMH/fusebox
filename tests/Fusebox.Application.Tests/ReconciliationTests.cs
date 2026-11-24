using Fusebox.Application.Reconciliation;
using Fusebox.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Application.Tests;

public class ReconciliationCalculatorTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-04-01T00:00:00Z");

    private static Money Usd(decimal dollars) => Money.FromDollars(dollars);

    private static ReconciliationReport Compare(Dictionary<string, Money> fusebox, params VendorLine[] lines) =>
        ReconciliationCalculator.Compare(Start, End, fusebox, lines);

    [Fact]
    public void A_total_within_the_tolerance_matches()
    {
        var report = Compare(new() { ["m"] = Usd(101m) }, new VendorLine(null, Usd(100m)));

        Assert.Equal(DriftStatus.Matches, report.Total.Status);
        Assert.Equal(0.01, report.Total.Drift!.Value, precision: 6);
        Assert.Null(report.Total.SuggestedFactor);
        Assert.Empty(report.Models);
    }

    [Fact]
    public void Charging_less_than_the_vendor_billed_is_reported_with_the_factor_that_would_close_the_gap()
    {
        var report = Compare(new() { ["m"] = Usd(97m) }, new VendorLine(null, Usd(100m)));

        Assert.Equal(DriftStatus.FuseboxUnder, report.Total.Status);
        Assert.Equal(-0.03, report.Total.Drift!.Value, precision: 6);
        Assert.Equal(100d / 97d, report.Total.SuggestedFactor!.Value, precision: 6);
    }

    [Fact]
    public void Charging_more_than_the_vendor_billed_is_reported_too()
    {
        var report = Compare(new() { ["m"] = Usd(120m) }, new VendorLine(null, Usd(100m)));

        Assert.Equal(DriftStatus.FuseboxOver, report.Total.Status);
        Assert.True(report.Total.SuggestedFactor < 1);
    }

    [Fact]
    public void Small_amounts_are_not_judged_because_a_few_cents_is_a_large_percentage()
    {
        var report = Compare(new() { ["m"] = Usd(0.30m) }, new VendorLine(null, Usd(0.60m)));

        Assert.Equal(DriftStatus.TooSmallToJudge, report.Total.Status);
        Assert.Null(report.Total.SuggestedFactor);
    }

    [Fact]
    public void A_line_per_model_is_compared_model_by_model_and_the_total_covers_all_of_them()
    {
        var report = Compare(
            new() { ["gpt-a"] = Usd(100m), ["gpt-b"] = Usd(50m) },
            new VendorLine("gpt-a", Usd(100m)),
            new VendorLine("gpt-b", Usd(60m)));

        Assert.Equal(DriftStatus.Matches, report.Models.Single(m => m.Model == "gpt-a").Status);
        Assert.Equal(DriftStatus.FuseboxUnder, report.Models.Single(m => m.Model == "gpt-b").Status);
        Assert.Equal(Usd(150m), report.Total.Fusebox);
        Assert.Equal(Usd(160m), report.Total.Vendor);
    }

    [Fact]
    public void Model_names_match_ignoring_case_but_are_never_guessed_at()
    {
        var report = Compare(
            new() { ["GPT-A"] = Usd(100m), ["gpt-b"] = Usd(50m) },
            new VendorLine("gpt-a", Usd(100m)),
            new VendorLine("gpt-b-2024-08-06", Usd(50m)));

        Assert.Equal(DriftStatus.Matches, report.Models.Single(m => string.Equals(m.Model, "gpt-a", StringComparison.OrdinalIgnoreCase)).Status);
        Assert.Equal(DriftStatus.NotOnStatement, report.Models.Single(m => m.Model == "gpt-b").Status);
        Assert.Equal(DriftStatus.NotRecordedByFusebox, report.Models.Single(m => m.Model == "gpt-b-2024-08-06").Status);
    }

    [Fact]
    public void A_vendor_that_billed_nothing_has_no_drift_to_report()
    {
        var report = Compare(new() { ["m"] = Usd(10m) }, new VendorLine(null, Usd(0m)));

        Assert.Null(report.Total.Drift);
    }
}

public class ReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-04-10T12:30:00Z");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-04-01T00:00:00Z");

    private readonly StubReader _reader = new();
    private readonly ReconciliationService _service;

    public ReconciliationServiceTests() =>
        _service = new ReconciliationService(_reader, new FakeTimeProvider(Now), NullLogger<ReconciliationService>.Instance);

    private ValueTask<ReconciliationReport> RunAsync(DateTimeOffset start, DateTimeOffset end, params VendorLine[] lines) =>
        _service.ReconcileAsync(new ReconciliationRequest(start, end, lines), CancellationToken.None);

    [Fact]
    public async Task Compares_the_period_with_what_was_recorded()
    {
        _reader.Costs["m"] = Money.FromDollars(100m);

        var report = await RunAsync(Start, End, new VendorLine(null, Money.FromDollars(100m)));

        Assert.Equal(DriftStatus.Matches, report.Total.Status);
        Assert.Equal((Start, End), _reader.Asked);
    }

    [Fact]
    public async Task A_period_that_does_not_start_and_end_on_the_hour_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await RunAsync(Start.AddMinutes(30), End, new VendorLine(null, Money.FromDollars(1m))));
        await Assert.ThrowsAsync<ArgumentException>(async () => await RunAsync(Start, End.AddMinutes(1), new VendorLine(null, Money.FromDollars(1m))));
    }

    [Fact]
    public async Task A_period_that_is_backwards_or_far_too_long_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await RunAsync(End, Start, new VendorLine(null, Money.FromDollars(1m))));
        await Assert.ThrowsAsync<ArgumentException>(async () => await RunAsync(Start.AddYears(-2), End, new VendorLine(null, Money.FromDollars(1m))));
    }

    [Fact]
    public async Task A_period_that_has_not_finished_being_recorded_is_refused()
    {
        var recent = DateTimeOffset.Parse("2026-04-10T11:00:00Z");

        await Assert.ThrowsAsync<ArgumentException>(async () => await RunAsync(Start, recent, new VendorLine(null, Money.FromDollars(1m))));
    }

    [Fact]
    public async Task A_statement_that_mixes_a_total_with_models_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await RunAsync(Start, End, new VendorLine(null, Money.FromDollars(1m)), new VendorLine("m", Money.FromDollars(1m))));
    }

    [Fact]
    public async Task A_model_listed_twice_or_a_blank_model_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await RunAsync(Start, End, new VendorLine("m", Money.FromDollars(1m)), new VendorLine("M", Money.FromDollars(2m))));
        await Assert.ThrowsAsync<ArgumentException>(async () => await RunAsync(Start, End, new VendorLine(" ", Money.FromDollars(1m))));
    }

    [Fact]
    public async Task An_empty_statement_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await RunAsync(Start, End));
    }

    private sealed class StubReader : IUsageCostReader
    {
        public Dictionary<string, Money> Costs { get; } = [];

        public (DateTimeOffset Start, DateTimeOffset End)? Asked { get; private set; }

        public ValueTask<IReadOnlyDictionary<string, Money>> CostByModelAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken)
        {
            Asked = (periodStart, periodEnd);
            return ValueTask.FromResult<IReadOnlyDictionary<string, Money>>(Costs);
        }
    }
}
