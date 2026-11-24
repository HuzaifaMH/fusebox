using Fusebox.Application.Detection;

namespace Fusebox.Application.Tests;

public class RobustStatisticsTests
{
    [Fact]
    public void The_median_of_an_odd_count_is_the_middle_value()
    {
        Assert.Equal(2, RobustStatistics.Median([3, 1, 2]));
    }

    [Fact]
    public void The_median_of_an_even_count_is_the_average_of_the_middle_two()
    {
        Assert.Equal(2.5, RobustStatistics.Median([4, 1, 3, 2]));
    }

    [Fact]
    public void The_median_of_nothing_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => RobustStatistics.Median([]));
    }

    [Fact]
    public void One_wild_value_does_not_move_the_median_or_the_deviation()
    {
        double[] values = [1, 2, 3, 4, 100];

        Assert.Equal(3, RobustStatistics.Median(values));
        Assert.Equal(1, RobustStatistics.MedianAbsoluteDeviation(values));
    }

    [Fact]
    public void Estimates_a_latency_percentile_inside_a_bucket()
    {
        // 50 calls up to 50 ms and 50 calls from 50 to 100 ms.
        long[] counts = [50, 50, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(50, RobustStatistics.LatencyPercentile(counts, 0.5));
        Assert.Equal(95, RobustStatistics.LatencyPercentile(counts, 0.95));
    }

    [Fact]
    public void Has_no_latency_percentile_without_calls()
    {
        Assert.Null(RobustStatistics.LatencyPercentile(new long[10], 0.95));
    }

    [Fact]
    public void Reports_the_lower_bound_of_the_open_ended_last_bucket()
    {
        long[] counts = [0, 0, 0, 0, 0, 0, 0, 0, 0, 10];

        Assert.Equal(30_000, RobustStatistics.LatencyPercentile(counts, 0.95));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    public void Rejects_a_percentile_outside_zero_to_one(double percentile)
    {
        Assert.ThrowsAny<ArgumentException>(() => RobustStatistics.LatencyPercentile(new long[10], percentile));
    }
}

public class SignalExtractorTests
{
    private static readonly DateTimeOffset Start = Traffic.Origin;

    private static MinuteStats Stats(int minute, long requests, long forwarded, long errors = 0, long inTokens = 0, long outTokens = 0, long cost = 0, long[]? histogram = null) =>
        new(Start.AddMinutes(minute), requests, forwarded, errors, inTokens, outTokens, cost, histogram ?? new long[10]);

    [Fact]
    public void Volume_and_spend_are_always_measurable()
    {
        var minute = Stats(0, requests: 7, forwarded: 0, cost: 1_500);

        Assert.Equal(7, SignalExtractor.Value(Signal.Volume, minute, minForwarded: 3));
        Assert.Equal(1_500, SignalExtractor.Value(Signal.Spend, minute, minForwarded: 3));
    }

    [Fact]
    public void Rates_are_computed_over_forwarded_calls()
    {
        var minute = Stats(0, requests: 12, forwarded: 10, errors: 3, inTokens: 2_000, outTokens: 500);

        Assert.Equal(0.3, SignalExtractor.Value(Signal.ErrorRate, minute, 3));
        Assert.Equal(200, SignalExtractor.Value(Signal.InputTokensPerRequest, minute, 3));
        Assert.Equal(50, SignalExtractor.Value(Signal.OutputTokensPerRequest, minute, 3));
    }

    [Fact]
    public void A_minute_with_too_few_forwarded_calls_cannot_say_anything_about_rates()
    {
        var minute = Stats(0, requests: 2, forwarded: 2, errors: 2);

        Assert.Null(SignalExtractor.Value(Signal.ErrorRate, minute, minForwarded: 3));
        Assert.Null(SignalExtractor.Value(Signal.LatencyP95, minute, minForwarded: 3));
        Assert.Null(SignalExtractor.Value(Signal.InputTokensPerRequest, minute, minForwarded: 3));
    }

    [Fact]
    public void Reads_latency_from_the_histogram()
    {
        var minute = Stats(0, 10, 10, histogram: [10, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        Assert.Equal(47.5, SignalExtractor.Value(Signal.LatencyP95, minute, 3));
    }

    [Fact]
    public void A_missing_minute_counts_as_zero_traffic_from_the_first_minute_that_has_a_row()
    {
        var minutes = new Dictionary<DateTimeOffset, MinuteStats>
        {
            [Start.AddMinutes(1)] = Stats(1, 5, 5),
            [Start.AddMinutes(4)] = Stats(4, 9, 9),
        };

        var values = SignalExtractor.Values(Signal.Volume, minutes, Start, Start.AddMinutes(6), zeroFillFrom: null, minForwarded: 3);

        // Minute 0 is before the series is known to exist, so it is left out. Minutes 2, 3 and 5 are real zeros.
        Assert.Equal([5, 0, 0, 9, 0], values);
    }

    [Fact]
    public void Can_be_told_where_to_start_counting_zeros()
    {
        var minutes = new Dictionary<DateTimeOffset, MinuteStats> { [Start.AddMinutes(2)] = Stats(2, 5, 5) };

        var values = SignalExtractor.Values(Signal.Volume, minutes, Start, Start.AddMinutes(4), zeroFillFrom: Start, minForwarded: 3);

        Assert.Equal([0, 0, 5, 0], values);
    }

    [Fact]
    public void A_rate_is_not_invented_for_a_minute_with_no_traffic()
    {
        var minutes = new Dictionary<DateTimeOffset, MinuteStats> { [Start] = Stats(0, 10, 10, errors: 5) };

        var values = SignalExtractor.Values(Signal.ErrorRate, minutes, Start, Start.AddMinutes(5), zeroFillFrom: Start, minForwarded: 3);

        Assert.Equal([0.5], values);
    }
}

public class BaselineBuilderTests
{
    private readonly DetectionOptions _options = new();

    // A Wednesday, ten past nine.
    private static readonly DateTimeOffset WindowStart = Traffic.Origin.AddHours(9).AddMinutes(10);

    private static Dictionary<DateTimeOffset, MinuteStats> Steady(DateTimeOffset from, int minutes, long requests) =>
        Enumerable.Range(0, minutes)
            .Select(i => Traffic.Minute(from.AddMinutes(i), new Shape(requests), new Random(i), noise: 0))
            .ToDictionary(m => m.Bucket);

    [Fact]
    public void Learns_what_is_normal_from_the_last_few_hours()
    {
        var minutes = Steady(WindowStart.AddMinutes(-40), 40, requests: 100);

        var baseline = BaselineBuilder.Build(Signal.Volume, minutes, WindowStart, _options);

        Assert.NotNull(baseline);
        Assert.Equal(BaselineBasis.Recent, baseline.Basis);
        Assert.Equal(100, baseline.Median);
        Assert.Equal(0, baseline.Mad);
        Assert.Equal(40, baseline.Samples);
    }

    [Fact]
    public void Has_no_baseline_until_there_is_enough_history()
    {
        var minutes = Steady(WindowStart.AddMinutes(-10), 10, requests: 100);

        Assert.Null(BaselineBuilder.Build(Signal.Volume, minutes, WindowStart, _options));
    }

    [Fact]
    public void Prefers_the_same_hour_of_the_week_when_there_are_enough_earlier_weeks()
    {
        // Busy at this hour in each of the last three weeks, quiet in the hours just before.
        var minutes = Steady(WindowStart.AddHours(-3), 180, requests: 10);
        var hourStart = Traffic.Origin.AddHours(9);
        for (var week = 1; week <= 3; week++)
        {
            foreach (var minute in Steady(hourStart.AddDays(-7 * week), 60, requests: 100))
            {
                minutes[minute.Key] = minute.Value;
            }
        }

        var baseline = BaselineBuilder.Build(Signal.Volume, minutes, WindowStart, _options);

        Assert.NotNull(baseline);
        Assert.Equal(BaselineBasis.HourOfWeek, baseline.Basis);
        Assert.Equal(100, baseline.Median);
        Assert.Equal(180, baseline.Samples);
    }

    [Fact]
    public void Falls_back_to_the_recent_hours_when_earlier_weeks_do_not_give_enough_samples()
    {
        // One earlier week gives 60 minutes, and the default needs 120.
        var minutes = Steady(WindowStart.AddHours(-3), 180, requests: 10);
        foreach (var minute in Steady(Traffic.Origin.AddHours(9).AddDays(-7), 60, requests: 100))
        {
            minutes[minute.Key] = minute.Value;
        }

        var baseline = BaselineBuilder.Build(Signal.Volume, minutes, WindowStart, _options);

        Assert.Equal(BaselineBasis.Recent, baseline!.Basis);
        Assert.Equal(10, baseline.Median);
    }

    [Fact]
    public void Ignores_other_hours_of_the_week()
    {
        // Busy at 14:00 in earlier weeks, which says nothing about 09:00.
        var minutes = Steady(WindowStart.AddHours(-3), 180, requests: 10);
        for (var week = 1; week <= 3; week++)
        {
            foreach (var minute in Steady(Traffic.Origin.AddHours(14).AddDays(-7 * week), 60, requests: 100))
            {
                minutes[minute.Key] = minute.Value;
            }
        }

        var baseline = BaselineBuilder.Build(Signal.Volume, minutes, WindowStart, _options);

        Assert.Equal(BaselineBasis.Recent, baseline!.Basis);
        Assert.Equal(10, baseline.Median);
    }

    [Fact]
    public void A_spike_in_the_history_does_not_raise_the_baseline()
    {
        var minutes = Steady(WindowStart.AddMinutes(-60), 60, requests: 100);
        for (var i = 30; i < 35; i++)
        {
            minutes[WindowStart.AddMinutes(-60 + i)] = Traffic.Minute(WindowStart.AddMinutes(-60 + i), new Shape(5_000), new Random(i), noise: 0);
        }

        var baseline = BaselineBuilder.Build(Signal.Volume, minutes, WindowStart, _options);

        Assert.Equal(100, baseline!.Median);
    }
}

public class AnomalyEngineTests
{
    private static readonly DateTimeOffset Now = Traffic.Origin.AddHours(10);

    private readonly DetectionOptions _options = new();
    private readonly AnomalyEngine _engine;

    public AnomalyEngineTests()
    {
        _engine = new AnomalyEngine(_options);
    }

    private static Observation Steady(double? current, long requests = 100, double median = 100, double mad = 5, BaselineBasis basis = BaselineBasis.Recent) =>
        new(current, requests, new Baseline(median, mad, Samples: 120, basis));

    private Verdict Evaluate(Signal signal, Observation observation, OpenAnomaly? open = null, DateTimeOffset? lastClosed = null) =>
        _engine.Evaluate(signal, observation, open, lastClosed, Now);

    private static OpenAnomaly Open(Signal signal, double baseline = 100, int calm = 0, BaselineBasis basis = BaselineBasis.Recent) =>
        new(Guid.NewGuid(), Traffic.Acme, signal, Now.AddMinutes(-30), baseline, basis, baseline * 5, baseline * 5, 5, calm, Now.AddMinutes(-1));

    [Fact]
    public void Starts_an_anomaly_when_a_signal_reaches_four_times_its_baseline()
    {
        var verdict = Evaluate(Signal.Volume, Steady(current: 400));

        var opened = Assert.IsType<Opened>(verdict);
        Assert.Equal(4, opened.Finding.Ratio);
        Assert.Equal(100, opened.Finding.Baseline);
        Assert.Equal(400, opened.Finding.Current);
        Assert.Equal(BaselineBasis.Recent, opened.Finding.Basis);
    }

    [Fact]
    public void Says_nothing_below_four_times_the_baseline()
    {
        Assert.IsType<NoChange>(Evaluate(Signal.Volume, Steady(current: 350)));
    }

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void The_minimum_volume_gate_needs_enough_requests_in_the_window(long requests, bool fires)
    {
        var verdict = Evaluate(Signal.Volume, Steady(current: 400, requests: requests));

        Assert.Equal(fires, verdict is Opened);
    }

    [Fact]
    public void A_big_ratio_on_tiny_numbers_is_not_an_anomaly_because_the_absolute_increase_is_too_small()
    {
        // Volume from 2 to 9 per minute is 4.5 times, but only 7 requests more, and 10 are needed.
        var verdict = Evaluate(Signal.Volume, Steady(current: 9, median: 2, mad: 0.5));

        Assert.IsType<NoChange>(verdict);
    }

    [Fact]
    public void A_baseline_of_zero_is_measured_against_the_floor_instead_of_dividing_by_zero()
    {
        var verdict = Evaluate(Signal.Volume, Steady(current: 12, median: 0, mad: 0));

        Assert.Equal(12, Assert.IsType<Opened>(verdict).Finding.Ratio);
    }

    [Fact]
    public void A_noisy_series_needs_to_be_far_outside_its_usual_spread_as_well_as_four_times_its_baseline()
    {
        // Median 100 and MAD 60 mean a normal minute varies by about 89, so 400 is only 3.4 spreads out.
        Assert.IsType<NoChange>(Evaluate(Signal.Volume, Steady(current: 400, mad: 60)));

        // With MAD 40 the spread is 59 and 400 is 5 spreads out.
        Assert.IsType<Opened>(Evaluate(Signal.Volume, Steady(current: 400, mad: 40)));
    }

    [Fact]
    public void Says_nothing_when_the_signal_could_not_be_measured()
    {
        Assert.IsType<NoChange>(Evaluate(Signal.ErrorRate, Steady(current: null)));
    }

    [Fact]
    public void Without_a_baseline_a_signal_with_no_fixed_limit_cannot_be_judged()
    {
        Assert.IsType<NoChange>(Evaluate(Signal.Volume, new Observation(5_000, 5_000, null)));
        Assert.IsType<NoChange>(Evaluate(Signal.Spend, new Observation(5_000_000, 100, null)));
    }

    [Fact]
    public void Without_a_baseline_a_fixed_limit_stands_in_for_one()
    {
        var verdict = Evaluate(Signal.ErrorRate, new Observation(0.6, 100, null));

        var opened = Assert.IsType<Opened>(verdict);
        Assert.Equal(BaselineBasis.Fixed, opened.Finding.Basis);
        Assert.Equal(0.5, opened.Finding.Baseline);
        Assert.Equal(1.2, opened.Finding.Ratio, 6);
    }

    [Theory]
    [InlineData(Signal.ErrorRate, 0.4, false)]
    [InlineData(Signal.ErrorRate, 0.5, true)]
    [InlineData(Signal.LatencyP95, 9_999, false)]
    [InlineData(Signal.LatencyP95, 12_000, true)]
    public void The_fixed_limits_apply_only_at_or_above_the_limit(Signal signal, double value, bool fires)
    {
        Assert.Equal(fires, Evaluate(signal, new Observation(value, 100, null)) is Opened);
    }

    [Fact]
    public void The_minimum_volume_gate_applies_to_fixed_limits_too()
    {
        Assert.IsType<NoChange>(Evaluate(Signal.ErrorRate, new Observation(0.9, 10, null)));
    }

    [Fact]
    public void A_signal_that_just_ended_cannot_start_again_until_the_cooldown_is_over()
    {
        Assert.IsType<NoChange>(Evaluate(Signal.Volume, Steady(current: 400), lastClosed: Now.AddMinutes(-10)));
        Assert.IsType<Opened>(Evaluate(Signal.Volume, Steady(current: 400), lastClosed: Now.AddMinutes(-16)));
    }

    [Fact]
    public void An_anomaly_stays_open_while_the_signal_is_between_the_clear_and_fire_ratios()
    {
        // 2.5 times the baseline is below the 4x that started it, but not yet back to the 2x that ends it.
        var verdict = Evaluate(Signal.Volume, Steady(current: 250), Open(Signal.Volume));

        var continuing = Assert.IsType<Continuing>(verdict);
        Assert.Equal(2.5, continuing.Ratio);
    }

    [Fact]
    public void An_anomaly_that_has_calmed_down_ends_only_after_enough_calm_evaluations_in_a_row()
    {
        var calm = Steady(current: 150);

        Assert.Equal(1, Assert.IsType<Calming>(Evaluate(Signal.Volume, calm, Open(Signal.Volume, calm: 0))).CalmEvaluations);
        Assert.Equal(2, Assert.IsType<Calming>(Evaluate(Signal.Volume, calm, Open(Signal.Volume, calm: 1))).CalmEvaluations);
        Assert.IsType<Closed>(Evaluate(Signal.Volume, calm, Open(Signal.Volume, calm: 2)));
    }

    [Fact]
    public void An_anomaly_is_judged_against_the_baseline_it_started_with_not_one_that_has_learned_the_anomaly()
    {
        // The recent history now looks like the anomaly (median 500), but the open anomaly remembers 100.
        var verdict = Evaluate(Signal.Volume, Steady(current: 500, median: 500, mad: 5), Open(Signal.Volume, baseline: 100));

        Assert.IsType<Continuing>(verdict);
    }

    [Fact]
    public void An_anomaly_on_a_series_that_has_gone_quiet_counts_as_calm_so_it_can_end()
    {
        var verdict = Evaluate(Signal.Volume, new Observation(null, 0, null), Open(Signal.Volume, calm: 2));

        Assert.IsType<Closed>(verdict);
    }

    [Fact]
    public void An_anomaly_started_by_a_fixed_limit_ends_once_the_signal_is_well_under_the_limit()
    {
        var open = Open(Signal.ErrorRate, baseline: 0.5, basis: BaselineBasis.Fixed);

        // 0.3 is 60% of the limit, still more than the half it needs to fall to.
        Assert.IsType<Continuing>(Evaluate(Signal.ErrorRate, new Observation(0.3, 100, null), open));
        Assert.IsType<Calming>(Evaluate(Signal.ErrorRate, new Observation(0.2, 100, null), open));
    }

    [Fact]
    public void Every_signal_has_its_own_absolute_limits()
    {
        // Spend must rise by $0.25 a minute: from $0.05 to $0.30 is six times, and enough.
        var spend = Evaluate(Signal.Spend, Steady(current: 300_000, median: 50_000, mad: 5_000));
        Assert.IsType<Opened>(spend);

        // From $0.01 to $0.06 is also six times, but only $0.05 more.
        Assert.IsType<NoChange>(Evaluate(Signal.Spend, Steady(current: 60_000, median: 10_000, mad: 1_000)));
    }
}

public class AnomalyDescriptionTests
{
    private readonly DetectionOptions _options = new();

    [Fact]
    public void Reads_as_a_complete_statement_with_the_numbers_that_triggered_it()
    {
        var finding = new Finding(Signal.Spend, 4_100_000, 1_000_000, 4.1, 9, BaselineBasis.Recent, 412);

        var text = AnomalyDescription.Of(Traffic.Acme, finding, _options);

        Assert.Equal(
            "Spend per minute for acme on openai-chat is $4.10 versus a baseline of $1.00 (4.1x, threshold 4x, compared with the last few hours), from 412 requests in the last 5 minutes.",
            text);
    }

    [Fact]
    public void Says_so_when_the_comparison_is_a_fixed_limit()
    {
        var finding = new Finding(Signal.ErrorRate, 0.62, 0.5, 1.24, 0, BaselineBasis.Fixed, 80);

        var text = AnomalyDescription.Of(Traffic.Acme, finding, _options);

        Assert.Contains("62%", text, StringComparison.Ordinal);
        Assert.Contains("a fixed limit of 50%", text, StringComparison.Ordinal);
        Assert.Contains("no history yet", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Signal.Volume, 1234.5, "1,234.5 requests")]
    [InlineData(Signal.LatencyP95, 4200, "4,200 ms")]
    [InlineData(Signal.InputTokensPerRequest, 18000, "18,000 tokens")]
    [InlineData(Signal.ErrorRate, 0.375, "37.5%")]
    [InlineData(Signal.Spend, 2_500_000, "$2.50")]
    public void Shows_each_signal_in_its_own_unit(Signal signal, double value, string expected)
    {
        Assert.Equal(expected, AnomalyDescription.Format(signal, value));
    }
}
