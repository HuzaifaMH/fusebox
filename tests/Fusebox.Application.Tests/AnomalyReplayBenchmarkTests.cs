using Fusebox.Application.Detection;
using Fusebox.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Fusebox.Application.Tests;

/// <summary>
/// The replay benchmark: synthetic traffic with faults injected at a known minute, played through the real detector
/// one minute at a time. It reports how long each fault took to detect and whether steady traffic raises false alarms.
/// Every scenario runs over several random seeds, and the assertions hold for the worst of them.
/// Traffic is roughly 100 requests a minute with 15% random variation, about $0.20 a minute.
/// </summary>
public class AnomalyReplayBenchmarkTests(ITestOutputHelper output)
{
    private static readonly int[] Seeds = [1, 2, 3, 4, 5];

    // Faults begin at 12:00 on day 2, after more than a day of normal traffic.
    private static readonly DateTimeOffset FaultStart = Traffic.Origin.AddDays(1).AddHours(12);

    private static readonly Shape Normal = new(Requests: 100);

    private sealed class Replay
    {
        public Replay(DetectionOptions? options = null)
        {
            Options = options ?? new DetectionOptions();
            Detector = new AnomalyDetector(Store, [Listener], Microsoft.Extensions.Options.Options.Create(Options), NullLogger<AnomalyDetector>.Instance);
        }

        public FakeAnomalyStore Store { get; } = new();

        public RecordingAnomalyListener Listener { get; } = new();

        public DetectionOptions Options { get; }

        public AnomalyDetector Detector { get; }

        public void Generate(DateTimeOffset from, int minutes, Func<int, Shape> shape, Random random, SeriesKey? series = null, double noise = 0.15)
        {
            for (var i = 0; i < minutes; i++)
            {
                Store.Add(series ?? Traffic.Acme, Traffic.Minute(from.AddMinutes(i), shape(i), random, noise));
            }
        }

        public async Task PlayAsync(DateTimeOffset from, DateTimeOffset to)
        {
            for (var now = from; now <= to; now = now.AddMinutes(1))
            {
                await Detector.RunAsync(now, CancellationToken.None);
            }
        }

        /// <summary>Minutes from <paramref name="faultStart"/> until the signal was first reported, or null if it never was.</summary>
        public double? MinutesToDetect(Signal signal, DateTimeOffset faultStart) =>
            Listener.Opened.Where(o => o.Finding.Signal == signal && o.At >= faultStart).Select(o => (double?)(o.At - faultStart).TotalMinutes).FirstOrDefault();
    }

    /// <summary>Traffic with a fault from <see cref="FaultStart"/> for <paramref name="faultMinutes"/>, then normal again.</summary>
    private static async Task<Replay> RunFaultAsync(int seed, Func<int, Shape> shapeAfterFaultStart, int faultMinutes)
    {
        var random = new Random(seed);
        var replay = new Replay();
        var hoursBefore = 36;
        var start = FaultStart.AddHours(-hoursBefore);

        // The traffic is generated for the whole run, and the detector only ever looks at minutes that have passed.
        replay.Generate(
            start,
            (hoursBefore * 60) + faultMinutes + 60,
            minute => minute < hoursBefore * 60 || minute >= (hoursBefore * 60) + faultMinutes ? Normal : shapeAfterFaultStart(minute - (hoursBefore * 60)),
            random);

        await replay.PlayAsync(FaultStart.AddHours(-2), FaultStart.AddMinutes(faultMinutes + 40));
        return replay;
    }

    private void Report(string scenario, IReadOnlyList<double?> minutes)
    {
        var detected = minutes.Where(m => m.HasValue).Select(m => m!.Value).OrderBy(m => m).ToList();
        output.WriteLine($"[benchmark] {scenario}: detected in {detected.Count}/{minutes.Count} runs, minutes to detect min {(detected.Count > 0 ? detected[0] : double.NaN)}, median {(detected.Count > 0 ? detected[detected.Count / 2] : double.NaN)}, max {(detected.Count > 0 ? detected[^1] : double.NaN)}");
    }

    private static async Task<IReadOnlyList<double?>> TimesToDetectAsync(Signal signal, Func<int, Shape> fault, int faultMinutes = 120)
    {
        var results = new List<double?>();

        foreach (var seed in Seeds)
        {
            var replay = await RunFaultAsync(seed, fault, faultMinutes);
            results.Add(replay.MinutesToDetect(signal, FaultStart));
        }

        return results;
    }

    [Fact]
    public async Task Steady_noisy_traffic_over_two_days_raises_no_false_alarms()
    {
        var falseAlarms = 0;

        foreach (var seed in Seeds)
        {
            var random = new Random(seed);
            var replay = new Replay();
            var bursts = Enumerable.Range(0, 2880).Where(_ => random.NextDouble() < 0.03).ToHashSet();

            // Normal traffic with a one-minute double-size burst now and then, and three-minute lulls to a quarter of normal.
            replay.Generate(
                Traffic.Origin,
                2880,
                minute => bursts.Contains(minute) ? Normal with { Requests = 200 } : (minute % 600) is >= 300 and < 303 ? Normal with { Requests = 25 } : Normal,
                random);

            await replay.PlayAsync(Traffic.Origin.AddHours(1), Traffic.Origin.AddMinutes(2880));
            falseAlarms += replay.Listener.Opened.Count;
        }

        output.WriteLine($"[benchmark] steady traffic: {Seeds.Length} runs of 48 hours, {falseAlarms} false alarms");
        Assert.Equal(0, falseAlarms);
    }

    [Fact]
    public async Task A_tenant_with_very_little_traffic_is_never_reported_however_noisy()
    {
        var random = new Random(11);
        var replay = new Replay();
        var tiny = new Shape(Requests: 3, ErrorRate: 0.3);

        replay.Generate(Traffic.Origin, 1440, _ => tiny, random, noise: 0.8);
        await replay.PlayAsync(Traffic.Origin.AddHours(1), Traffic.Origin.AddMinutes(1440));

        Assert.Empty(replay.Listener.Opened);
    }

    [Fact]
    public async Task A_fivefold_jump_in_cost_per_request_is_detected_within_five_minutes()
    {
        var times = await TimesToDetectAsync(Signal.Spend, _ => Normal with { CostPerRequestUsd = 0.01 });

        Report("cost per request x5 (a pricier model swapped in)", times);
        Assert.All(times, t => Assert.InRange(t ?? double.MaxValue, 0, 6));
    }

    [Fact]
    public async Task A_cost_jump_is_reported_as_spend_and_not_as_a_jump_in_volume()
    {
        var replay = await RunFaultAsync(1, _ => Normal with { CostPerRequestUsd = 0.01 }, 120);

        Assert.Null(replay.MinutesToDetect(Signal.Volume, FaultStart));
        Assert.NotNull(replay.MinutesToDetect(Signal.Spend, FaultStart));
    }

    [Fact]
    public async Task A_sixfold_surge_in_volume_is_detected_within_five_minutes()
    {
        var times = await TimesToDetectAsync(Signal.Volume, _ => Normal with { Requests = 600 });

        Report("request volume x6 (an unthrottled endpoint)", times);
        Assert.All(times, t => Assert.InRange(t ?? double.MaxValue, 0, 6));
    }

    [Fact]
    public async Task A_volume_surge_also_raises_spend()
    {
        var replay = await RunFaultAsync(2, _ => Normal with { Requests = 600 }, 120);

        Assert.NotNull(replay.MinutesToDetect(Signal.Spend, FaultStart));
    }

    [Fact]
    public async Task An_error_storm_is_detected_within_five_minutes()
    {
        var times = await TimesToDetectAsync(Signal.ErrorRate, _ => Normal with { ErrorRate = 0.6 });

        Report("error rate 1% -> 60%", times);
        Assert.All(times, t => Assert.InRange(t ?? double.MaxValue, 0, 6));
    }

    [Fact]
    public async Task A_slowdown_of_the_vendor_is_detected_within_five_minutes()
    {
        var times = await TimesToDetectAsync(Signal.LatencyP95, _ => Normal with { LatencyMedianMs = 6_000 });

        Report("latency median 400 ms -> 6 s", times);
        Assert.All(times, t => Assert.InRange(t ?? double.MaxValue, 0, 6));
    }

    [Fact]
    public async Task Prompts_growing_over_two_hours_are_detected_while_they_grow()
    {
        // Input tokens per request climb from 20 to 800 over two hours, a prompt that is no longer being truncated.
        var times = await TimesToDetectAsync(Signal.InputTokensPerRequest, minute => Normal with { InputTokens = 20 + (780.0 * minute / 120) });

        Report("input tokens per request 20 -> 800 over 2 hours", times);
        Assert.All(times, t => Assert.InRange(t ?? double.MaxValue, 0, 120));
    }

    [Fact]
    public async Task A_lasting_fault_is_reported_once_not_every_minute()
    {
        var replay = await RunFaultAsync(3, _ => Normal with { CostPerRequestUsd = 0.01 }, 180);

        Assert.Single(replay.Listener.Opened, o => o.Finding.Signal == Signal.Spend);
    }

    [Fact]
    public async Task The_anomaly_ends_soon_after_the_fault_does()
    {
        var times = new List<double>();

        foreach (var seed in Seeds)
        {
            var replay = await RunFaultAsync(seed, _ => Normal with { CostPerRequestUsd = 0.01 }, 60);
            var ended = replay.Listener.Closed.Single(c => c.Anomaly.Signal == Signal.Spend).At;
            times.Add((ended - FaultStart.AddMinutes(60)).TotalMinutes);
        }

        output.WriteLine($"[benchmark] minutes from the end of a cost fault until its anomaly ends: min {times.Min()}, max {times.Max()}");
        Assert.All(times, t => Assert.InRange(t, 0, 13));
    }

    [Fact]
    public async Task A_slow_drift_over_many_hours_is_reported_with_how_far_it_got_before_it_was_noticed()
    {
        // Cost per request creeps from $0.002 to $0.02 over twelve hours. A baseline learned from the last six hours
        // keeps adapting as the drift goes on, so this measures how late (if at all) the drift is caught.
        var times = await TimesToDetectAsync(Signal.Spend, minute => Normal with { CostPerRequestUsd = 0.002 + (0.018 * minute / 720) }, faultMinutes: 720);

        Report("cost per request creeps x10 over 12 hours", times);
        Assert.NotEmpty(times);
    }

    [Fact]
    public async Task A_regular_daily_climb_is_not_a_false_alarm_when_earlier_weeks_are_known_but_is_when_they_are_not()
    {
        // Busy (100 a minute) from 09:00 to 17:00 on weekdays, quiet (10 a minute) otherwise. Three weeks of history,
        // then the fourth Wednesday morning is replayed around the 09:00 climb.
        var random = new Random(5);
        Shape ShapeAt(DateTimeOffset at) =>
            at.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && at.Hour is >= 9 and < 17 ? Normal : Normal with { Requests = 10 };

        var testDay = Traffic.Origin.AddDays(21);

        var withWeeks = new Replay();
        var withoutWeeks = new Replay(new DetectionOptions { SeasonalWeeks = 0 });

        for (var minute = 0; minute < 22 * 1440; minute++)
        {
            var at = Traffic.Origin.AddMinutes(minute);
            var stats = Traffic.Minute(at, ShapeAt(at), random);
            withWeeks.Store.Add(Traffic.Acme, stats);
            withoutWeeks.Store.Add(Traffic.Acme, stats);
        }

        await withWeeks.PlayAsync(testDay.AddHours(8), testDay.AddHours(10));
        await withoutWeeks.PlayAsync(testDay.AddHours(8), testDay.AddHours(10));

        output.WriteLine($"[benchmark] daily climb 10 -> 100 a minute at 09:00: {withWeeks.Listener.Opened.Count} alarms using earlier weeks, {withoutWeeks.Listener.Opened.Count} using only the last six hours");
        Assert.Empty(withWeeks.Listener.Opened);
        Assert.Contains(withoutWeeks.Listener.Opened, o => o.Finding.Signal == Signal.Volume);
    }

    [Fact]
    public async Task The_same_climb_is_judged_against_the_same_hour_in_earlier_weeks_when_it_is_reported()
    {
        var replay = new Replay(new DetectionOptions { SeasonalWeeks = 0 });
        var random = new Random(5);
        replay.Generate(Traffic.Origin, 12 * 60, minute => minute < 9 * 60 ? Normal with { Requests = 10 } : Normal, random);

        await replay.PlayAsync(Traffic.Origin.AddHours(8), Traffic.Origin.AddHours(10));

        var alarm = replay.Listener.Opened.First(o => o.Finding.Signal == Signal.Volume);
        Assert.Equal(BaselineBasis.Recent, alarm.Finding.Basis);
        Assert.Contains("the last few hours", alarm.Description, StringComparison.Ordinal);
    }
}
