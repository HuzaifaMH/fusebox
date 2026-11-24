using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;

namespace Fusebox.Application.Tests;

/// <summary>One model's traffic over a period: calls per minute, tokens per call, and the price of a million tokens.</summary>
internal sealed record ModelSpec(
    string Model,
    double CallsPerMinute,
    double InputTokens = 200,
    double OutputTokens = 100,
    double InputPricePerMillion = 1,
    double OutputPricePerMillion = 3,
    double ErrorRate = 0.01,
    double LatencyMedianMs = 400,
    double RefusedPerMinute = 0);

internal static class Periods
{
    public static readonly DateTimeOffset Start = Traffic.Origin.AddHours(12);

    public static PeriodTotals Of(int minutes, params ModelSpec[] models) => new(
        minutes,
        minutes,
        models.ToDictionary(m => m.Model, m => Totals(m, minutes)));

    public static ModelTotals Totals(ModelSpec spec, int minutes)
    {
        var forwarded = (long)Math.Round(spec.CallsPerMinute * minutes);
        var input = (long)Math.Round(forwarded * spec.InputTokens);
        var output = (long)Math.Round(forwarded * spec.OutputTokens);
        var refused = (long)Math.Round(spec.RefusedPerMinute * minutes);

        // A million tokens cost the stated number of dollars, which is that many micro-dollars per token.
        var cost = (long)Math.Round((input * spec.InputPricePerMillion) + (output * spec.OutputPricePerMillion));

        return new ModelTotals(
            forwarded + refused,
            forwarded,
            refused,
            0,
            0,
            0,
            (long)Math.Round(forwarded * spec.ErrorRate),
            input,
            output,
            cost,
            Traffic.LatencyCounts(forwarded, spec.LatencyMedianMs, new Random(1)));
    }
}

public class RateDecompositionTests
{
    [Fact]
    public void The_two_parts_add_up_exactly_to_the_change()
    {
        var before = new Dictionary<string, SegmentRate> { ["a"] = new(100, 1_000), ["b"] = new(900, 1_000) };
        var after = new Dictionary<string, SegmentRate> { ["a"] = new(150, 500), ["b"] = new(2_700, 1_500) };

        var change = RateDecomposition.Decompose(before, after)!;

        Assert.Equal(0.5, change.Before, 9);
        Assert.Equal(1.425, change.After, 9);
        Assert.Equal(change.Delta, change.RateEffect + change.MixEffect, 9);
        Assert.Equal(0, change.Unexplained, 9);
    }

    [Fact]
    public void A_shift_of_traffic_to_a_costlier_segment_is_a_mix_effect_not_a_rate_effect()
    {
        // Each segment's own ratio never changes: 1 for a, 10 for b. Only the traffic moves.
        var before = new Dictionary<string, SegmentRate> { ["a"] = new(900, 900), ["b"] = new(100, 10) };
        var after = new Dictionary<string, SegmentRate> { ["a"] = new(100, 100), ["b"] = new(900, 90) };

        var change = RateDecomposition.Decompose(before, after)!;

        Assert.Equal(0, change.RateEffect, 9);
        Assert.Equal(change.Delta, change.MixEffect, 9);
        Assert.True(change.Delta > 0);
    }

    [Fact]
    public void A_change_of_a_segments_own_ratio_is_a_rate_effect_not_a_mix_effect()
    {
        var before = new Dictionary<string, SegmentRate> { ["a"] = new(100, 100) };
        var after = new Dictionary<string, SegmentRate> { ["a"] = new(400, 100) };

        var change = RateDecomposition.Decompose(before, after)!;

        Assert.Equal(0, change.MixEffect, 9);
        Assert.Equal(3, change.RateEffect, 9);
    }

    [Fact]
    public void A_segment_that_appears_for_the_first_time_counts_as_mix_only()
    {
        var before = new Dictionary<string, SegmentRate> { ["a"] = new(100, 100) };
        var after = new Dictionary<string, SegmentRate> { ["a"] = new(100, 100), ["new"] = new(1_000, 100) };

        var change = RateDecomposition.Decompose(before, after)!;

        Assert.Equal(change.Delta, change.RateEffect + change.MixEffect, 9);
        Assert.Equal(0, change.RateEffect, 9);
    }

    [Fact]
    public void Cannot_be_computed_without_a_denominator_in_both_periods()
    {
        var some = new Dictionary<string, SegmentRate> { ["a"] = new(1, 1) };
        var none = new Dictionary<string, SegmentRate> { ["a"] = new(1, 0) };

        Assert.Null(RateDecomposition.Decompose(some, none));
        Assert.Null(RateDecomposition.Decompose(none, some));
    }

    [Fact]
    public void Holds_for_arbitrary_numbers()
    {
        var random = new Random(3);

        for (var run = 0; run < 200; run++)
        {
            SegmentRate Pick() => new(random.NextDouble() * 1_000, 1 + (random.NextDouble() * 500));
            var before = Enumerable.Range(0, 4).ToDictionary(i => $"m{i}", _ => Pick());
            var after = Enumerable.Range(1, 5).ToDictionary(i => $"m{i}", _ => Pick());

            var change = RateDecomposition.Decompose(before, after)!;

            Assert.Equal(change.Delta, change.RateEffect + change.MixEffect, 6);
        }
    }
}

/// <summary>
/// A golden set of incidents with a known cause. For each, the engine must name that cause as the biggest mover,
/// and the shares of the factors must add up to the whole change.
/// </summary>
public class EvidenceEngineTests
{
    private static readonly SeriesKey Series = Traffic.Acme;

    private readonly EvidenceEngine _engine = new(new DiagnosisOptions());

    private Evidence Analyze(Signal signal, PeriodTotals now, PeriodTotals before) =>
        _engine.Analyze(Series, signal, Periods.Start, Periods.Start.AddMinutes(now.PeriodMinutes), now, before);

    private static ModelSpec Mini(double calls = 100, double input = 200, double output = 100, double inPrice = 1, double outPrice = 3, double error = 0.01, double latency = 400, double refused = 0) =>
        new("mini", calls, input, output, inPrice, outPrice, error, latency, refused);

    private static ModelSpec Big(double calls = 100, double input = 200, double output = 100, double inPrice = 10, double outPrice = 30, double error = 0.01, double latency = 400) =>
        new("big", calls, input, output, inPrice, outPrice, error, latency);

    private static PeriodTotals Normal(params ModelSpec[] models) => Periods.Of(60, models.Length == 0 ? [Mini()] : models);

    private static PeriodTotals Window(params ModelSpec[] models) => Periods.Of(5, models);

    private static double SumOfFactorShares(Evidence evidence) => evidence.Factors.Sum(f => f.ShareOfChange!.Value);

    [Fact]
    public void A_surge_in_calls_is_named_as_the_cause_of_the_extra_spend()
    {
        // Calls per minute x6, for example a retry loop.
        var evidence = Analyze(Signal.Spend, Window(Mini(calls: 600)), Normal());

        Assert.Equal("spend.volume", evidence.TopMover!.Id);
        Assert.InRange(evidence.TopMover.ShareOfChange!.Value, 0.99, 1.01);
        Assert.Equal(1, SumOfFactorShares(evidence) + evidence.Unexplained, 6);
        Assert.InRange(evidence.TopMover.ChangeRatio!.Value, 5.99, 6.01);
    }

    [Fact]
    public void A_prompt_that_stopped_being_truncated_is_named_as_input_tokens()
    {
        var evidence = Analyze(Signal.Spend, Window(Mini(input: 4_000)), Normal());

        Assert.Equal("spend.inputTokens", evidence.TopMover!.Id);
        Assert.True(evidence.TopMover.ShareOfChange > 0.95);
        Assert.Equal(1, SumOfFactorShares(evidence) + evidence.Unexplained, 6);
    }

    [Fact]
    public void Longer_answers_are_named_as_output_tokens()
    {
        var evidence = Analyze(Signal.Spend, Window(Mini(output: 2_000)), Normal());

        // Output tokens cost more than input tokens, so the blended cost per token rises too. That is reported
        // separately and honestly as an effective cost, not as a change of price.
        Assert.Equal("spend.outputTokens", evidence.TopMover!.Id);
        Assert.InRange(evidence.TopMover.ShareOfChange!.Value, 0.7, 0.9);
        Assert.True(evidence.Items.Single(i => i.Id == "spend.pricePerToken").ShareOfChange > 0.1);
        Assert.Equal(1, SumOfFactorShares(evidence) + evidence.Unexplained, 6);
    }

    [Fact]
    public void A_move_to_a_pricier_model_is_named_as_model_mix_and_not_as_a_price_change()
    {
        // The same traffic and the same token counts, but all of it now goes to a model that costs ten times as much.
        var evidence = Analyze(Signal.Spend, Window(Big(calls: 100)), Normal(Mini(calls: 100)));

        Assert.Equal("spend.modelMix", evidence.TopMover!.Id);
        Assert.True(evidence.TopMover.ShareOfChange > 0.99);
        Assert.Equal(0, evidence.Items.Single(i => i.Id == "spend.pricePerToken").ShareOfChange!.Value, 6);
        Assert.Contains("big: 0% -> 100% of tokens", evidence.TopMover.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_price_change_on_the_same_model_is_named_as_price_per_token_and_not_as_model_mix()
    {
        var evidence = Analyze(Signal.Spend, Window(Mini(inPrice: 4, outPrice: 12)), Normal());

        Assert.Equal("spend.pricePerToken", evidence.TopMover!.Id);
        Assert.True(evidence.TopMover.ShareOfChange > 0.99);
        Assert.Equal(0, evidence.Items.Single(i => i.Id == "spend.modelMix").ShareOfChange!.Value, 6);
        Assert.Contains("mini: $", evidence.TopMover.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_causes_at_once_are_each_given_their_own_share_and_the_shares_add_up()
    {
        // Twice the calls and three times the input tokens.
        var evidence = Analyze(Signal.Spend, Window(Mini(calls: 200, input: 600)), Normal());

        var volume = evidence.Items.Single(i => i.Id == "spend.volume").ShareOfChange!.Value;
        var input = evidence.Items.Single(i => i.Id == "spend.inputTokens").ShareOfChange!.Value;

        Assert.InRange(volume, 0.2, 0.6);
        Assert.InRange(input, 0.3, 0.8);
        Assert.Equal(1, SumOfFactorShares(evidence) + evidence.Unexplained, 6);
        Assert.Equal(0, evidence.Unexplained, 6);
    }

    [Fact]
    public void Names_the_model_that_accounts_for_the_increase_in_spend()
    {
        var before = Normal(Mini(), Big(calls: 10));
        var now = Window(Mini(), Big(calls: 60));

        var evidence = Analyze(Signal.Spend, now, before);

        var top = evidence.Items.Where(i => i.Kind == EvidenceKind.Segment).First();
        Assert.Equal("model.big", top.Id);
        Assert.InRange(top.ShareOfChange!.Value, 0.95, 1.01);
    }

    [Fact]
    public void Reports_the_headline_change_as_a_fact_that_is_not_itself_a_cause()
    {
        var evidence = Analyze(Signal.Spend, Window(Mini(calls: 600)), Normal());

        var headline = evidence.Items.Single(i => i.Id == "spend.total");
        Assert.Equal(EvidenceKind.Context, headline.Kind);
        Assert.Equal("$/min", headline.Unit);
        Assert.InRange(headline.ChangeRatio!.Value, 5.99, 6.01);
        Assert.Equal(headline, evidence.Items[0]);
    }

    [Fact]
    public void Without_token_counts_only_calls_and_cost_per_call_can_be_told_apart()
    {
        // A flat-priced API: no tokens reported, $0.01 a call, and then $0.05 a call.
        static PeriodTotals Flat(double calls, double perCall) => new(
            5,
            5,
            new Dictionary<string, ModelTotals>
            {
                [string.Empty] = new((long)(calls * 5), (long)(calls * 5), 0, 0, 0, 0, 0, 0, 0, (long)(calls * 5 * perCall * 1_000_000), new long[10]),
            });

        var evidence = Analyze(Signal.Spend, Flat(100, 0.05), Flat(100, 0.01));

        Assert.Equal("spend.costPerCall", evidence.TopMover!.Id);
        Assert.DoesNotContain(evidence.Items, i => i.Id == "spend.inputTokens");
        Assert.Equal(1, SumOfFactorShares(evidence) + evidence.Unexplained, 6);
    }

    [Fact]
    public void A_surge_in_requests_is_split_by_what_became_of_them()
    {
        // Calls refused for budget rise from 0 to 400 a minute while forwarded calls stay put.
        var evidence = Analyze(Signal.Volume, Window(Mini(refused: 400)), Normal());

        Assert.Equal("volume.denied", evidence.TopMover!.Id);
        Assert.InRange(evidence.TopMover.ShareOfChange!.Value, 0.99, 1.01);
        Assert.Equal(1, SumOfFactorShares(evidence) + evidence.Unexplained, 6);
    }

    [Fact]
    public void An_error_storm_in_one_model_is_traced_to_that_model()
    {
        var before = Normal(Mini(), Big());
        var now = Window(Mini(), Big(error: 0.9));

        var evidence = Analyze(Signal.ErrorRate, now, before);

        var top = evidence.Items.Where(i => i.Kind == EvidenceKind.Segment).First();
        Assert.Equal("model.big", top.Id);
        Assert.True(top.ShareOfChange > 0.95);
        Assert.Equal("%", top.Unit);
        Assert.Equal(1, top.Before!.Value, 0);
        Assert.Equal(90, top.Now!.Value, 0);
    }

    [Fact]
    public void Errors_that_come_from_traffic_moving_to_a_model_that_fails_more_are_called_a_mix_effect()
    {
        var before = Normal(Mini(error: 0.01, calls: 90), Big(error: 0.5, calls: 10));
        var now = Window(Mini(error: 0.01, calls: 10), Big(error: 0.5, calls: 90));

        var evidence = Analyze(Signal.ErrorRate, now, before);

        Assert.Equal("errors.modelMix", evidence.Factors.First().Id);
        Assert.True(evidence.Factors.First().ShareOfChange > 0.99);
    }

    [Fact]
    public void A_slowdown_in_one_model_is_traced_to_it_with_the_latency_before_and_after()
    {
        var before = Normal(Mini(), Big());
        var now = Window(Mini(), Big(latency: 6_000));

        var evidence = Analyze(Signal.LatencyP95, now, before);

        var top = evidence.Items.First(i => i.Kind == EvidenceKind.Segment);
        Assert.Equal("model.big", top.Id);
        Assert.True(top.ShareOfChange > 0.9);
        Assert.True(top.Now > top.Before * 5);
    }

    [Fact]
    public void Longer_answers_are_offered_as_context_for_a_slowdown()
    {
        var evidence = Analyze(Signal.LatencyP95, Window(Mini(output: 2_000, latency: 5_000)), Normal());

        var context = evidence.Items.Single(i => i.Id == "context.outputTokens");
        Assert.Equal(100, context.Before!.Value, 0);
        Assert.Equal(2_000, context.Now!.Value, 0);
    }

    [Fact]
    public void Prompt_growth_in_one_model_is_traced_to_that_model()
    {
        var before = Normal(Mini(), Big());
        var now = Window(Mini(), Big(input: 6_000));

        var evidence = Analyze(Signal.InputTokensPerRequest, now, before);

        var top = evidence.Items.First(i => i.Kind == EvidenceKind.Segment);
        Assert.Equal("model.big", top.Id);
        Assert.True(top.ShareOfChange > 0.9);
        Assert.Equal(200, top.Before!.Value, 0);
        Assert.Equal(6_000, top.Now!.Value, 0);
    }

    [Fact]
    public void Marks_the_evidence_thin_when_the_window_or_the_comparison_has_little_data()
    {
        var thinWindow = Analyze(Signal.Spend, new PeriodTotals(2, 5, Window(Mini(calls: 600)).ByModel), Normal());
        var thinBefore = Analyze(Signal.Spend, Window(Mini(calls: 600)), new PeriodTotals(10, 60, Normal().ByModel));
        var fewCalls = Analyze(Signal.Spend, Window(Mini(calls: 1)), Normal());
        var solid = Analyze(Signal.Spend, Window(Mini(calls: 600)), Normal());

        Assert.True(thinWindow.IsThin);
        Assert.True(thinBefore.IsThin);
        Assert.True(fewCalls.IsThin);
        Assert.False(solid.IsThin);
    }

    [Fact]
    public void Says_what_it_cannot_decompose_instead_of_inventing_shares()
    {
        // No spend before the anomaly at all, so there is nothing to compare with.
        var evidence = Analyze(Signal.Spend, Window(Mini()), Periods.Of(60, Mini(calls: 0)));

        Assert.Null(evidence.TopMover);
        Assert.Equal(1, evidence.Unexplained);
    }

    [Fact]
    public void Lists_only_as_many_models_as_asked_for()
    {
        var models = Enumerable.Range(0, 8).Select(i => new ModelSpec($"m{i}", 10)).ToArray();
        var grown = Enumerable.Range(0, 8).Select(i => new ModelSpec($"m{i}", 10 + i)).ToArray();

        var evidence = Analyze(Signal.Spend, Window(grown), Normal(models));

        Assert.Equal(3, evidence.Items.Count(i => i.Kind == EvidenceKind.Segment));
    }
}
