using Fusebox.Application.Detection;
using Fusebox.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Tests;

/// <summary>
/// Rounds of detection against an in-memory store. Traffic is steady at 100 requests a minute costing $0.20 until
/// 02:00, when each request starts costing five times as much for five minutes.
/// </summary>
public class AnomalyDetectorTests
{
    private static readonly DateTimeOffset T0 = Traffic.Origin.AddHours(2);

    private readonly FakeAnomalyStore _store = new();
    private readonly RecordingAnomalyListener _listener = new();
    // These tests hand the detector complete minutes, so they use a one-minute lag to keep the rounds easy to follow.
    private readonly DetectionOptions _options = new() { Lag = TimeSpan.FromMinutes(1) };
    private readonly Random _random = new(7);

    private AnomalyDetector Create(params IAnomalyListener[] extra) => new(
        _store,
        [_listener, .. extra],
        Options.Create(_options),
        NullLogger<AnomalyDetector>.Instance);

    private void AddMinutes(SeriesKey series, DateTimeOffset from, int count, Shape shape)
    {
        for (var i = 0; i < count; i++)
        {
            _store.Add(series, Traffic.Minute(from.AddMinutes(i), shape, _random, noise: 0.05));
        }
    }

    private static readonly Shape Normal = new(Requests: 100);
    private static readonly Shape FiveTimesTheCost = new(Requests: 100, CostPerRequestUsd: 0.01);

    /// <summary>An hour of normal traffic, then five minutes with spend up fivefold, then normal again.</summary>
    private void SeedSpendSpike(SeriesKey? series = null)
    {
        var key = series ?? Traffic.Acme;
        AddMinutes(key, T0.AddMinutes(-60), 60, Normal);
        AddMinutes(key, T0, 5, FiveTimesTheCost);
        AddMinutes(key, T0.AddMinutes(5), 30, Normal);
    }

    /// <summary>The round that judges the minutes up to the one before <paramref name="minutesAfterT0"/>, allowing for the lag.</summary>
    private static DateTimeOffset Round(int minutesAfterT0) => T0.AddMinutes(minutesAfterT0);

    [Fact]
    public async Task Opens_an_anomaly_when_spend_jumps_and_says_what_happened()
    {
        SeedSpendSpike();

        await Create().RunAsync(Round(6), CancellationToken.None);

        var anomaly = Assert.Single(_store.Open);
        Assert.Equal(Signal.Spend, anomaly.Signal);
        Assert.Equal(Traffic.Acme, anomaly.Series);
        Assert.Equal(BaselineBasis.Recent, anomaly.Basis);
        Assert.InRange(anomaly.Baseline, 150_000, 250_000);
        Assert.InRange(anomaly.OpenedValue, 900_000, 1_100_000);
        Assert.InRange(anomaly.PeakRatio, 4.5, 5.5);

        var announced = Assert.Single(_listener.Opened);
        Assert.Equal(Round(6), announced.At);
        Assert.Contains("Spend per minute for acme on openai-chat", announced.Description, StringComparison.Ordinal);
        Assert.Contains("versus a baseline of $0.2", announced.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaves_other_signals_alone_when_only_spend_moved()
    {
        SeedSpendSpike();

        await Create().RunAsync(Round(6), CancellationToken.None);

        Assert.All(_store.Open, anomaly => Assert.Equal(Signal.Spend, anomaly.Signal));
    }

    [Fact]
    public async Task Says_nothing_while_traffic_stays_normal()
    {
        AddMinutes(Traffic.Acme, T0.AddMinutes(-60), 70, Normal);

        await Create().RunAsync(Round(6), CancellationToken.None);

        Assert.Empty(_store.Changes);
        Assert.Empty(_listener.Opened);
    }

    [Fact]
    public async Task Judges_only_minutes_that_are_old_enough_to_be_complete()
    {
        AddMinutes(Traffic.Acme, T0.AddMinutes(-60), 63, Normal);
        AddMinutes(Traffic.Acme, T0.AddMinutes(3), 3, FiveTimesTheCost);
        var detector = Create();

        // At 02:05:30 the newest minutes are 02:04 and 02:05, which are not complete yet. The round judges
        // the five minutes up to 02:04, of which only one (02:03) is elevated, so nothing is reported.
        await detector.RunAsync(T0.AddMinutes(5).AddSeconds(30), CancellationToken.None);
        Assert.Empty(_store.Open);

        // Two minutes later three of the judged minutes are elevated, and the anomaly is found.
        await detector.RunAsync(T0.AddMinutes(7).AddSeconds(30), CancellationToken.None);
        Assert.Single(_store.Open);
    }

    [Fact]
    public async Task Does_nothing_when_another_instance_is_already_running_detection()
    {
        SeedSpendSpike();
        _store.LockAvailable = false;

        await Create().RunAsync(Round(6), CancellationToken.None);

        Assert.Empty(_store.Changes);
        Assert.Equal(0, _store.HistoryLoads);
    }

    [Fact]
    public async Task Follows_an_anomaly_from_start_to_finish()
    {
        SeedSpendSpike();
        var detector = Create();

        await detector.RunAsync(Round(6), CancellationToken.None);
        Assert.Single(_store.Open);

        // Still elevated in two of the next rounds, because the window is five minutes wide.
        await detector.RunAsync(Round(7), CancellationToken.None);
        await detector.RunAsync(Round(8), CancellationToken.None);
        Assert.Equal(0, _store.Open.Single().CalmEvaluations);

        // Then three calm rounds in a row end it.
        await detector.RunAsync(Round(9), CancellationToken.None);
        Assert.Equal(1, _store.Open.Single().CalmEvaluations);
        await detector.RunAsync(Round(10), CancellationToken.None);
        Assert.Equal(2, _store.Open.Single().CalmEvaluations);
        await detector.RunAsync(Round(11), CancellationToken.None);

        Assert.Empty(_store.Open);
        var ended = Assert.Single(_listener.Closed);
        Assert.Equal(Signal.Spend, ended.Anomaly.Signal);
        Assert.Equal(Round(11), ended.At);
        Assert.Equal(Round(11), _store.Closed[(Traffic.Acme, Signal.Spend)]);
    }

    [Fact]
    public async Task Remembers_the_highest_point_the_anomaly_reached()
    {
        SeedSpendSpike();
        var detector = Create();
        await detector.RunAsync(Round(6), CancellationToken.None);
        var opened = _store.Open.Single();

        await detector.RunAsync(Round(7), CancellationToken.None);

        Assert.True(_store.Open.Single().PeakValue >= opened.OpenedValue);
        Assert.True(_store.Open.Single().PeakRatio >= opened.PeakRatio);
    }

    [Fact]
    public async Task A_signal_that_just_ended_cannot_start_a_new_anomaly_during_the_cooldown()
    {
        SeedSpendSpike();
        var detector = Create();
        foreach (var minute in new[] { 6, 7, 8, 9, 10, 11 })
        {
            await detector.RunAsync(Round(minute), CancellationToken.None);
        }

        Assert.Empty(_store.Open);

        // Spend jumps again five minutes later, inside the cooldown.
        AddMinutes(Traffic.Acme, T0.AddMinutes(15), 5, FiveTimesTheCost);
        await detector.RunAsync(Round(21), CancellationToken.None);

        Assert.Empty(_store.Open);
    }

    [Fact]
    public async Task Can_start_a_new_anomaly_once_the_cooldown_has_passed()
    {
        SeedSpendSpike();
        var detector = Create();
        foreach (var minute in new[] { 6, 7, 8, 9, 10, 11 })
        {
            await detector.RunAsync(Round(minute), CancellationToken.None);
        }

        AddMinutes(Traffic.Acme, T0.AddMinutes(30), 10, FiveTimesTheCost);
        await detector.RunAsync(Round(37), CancellationToken.None);

        Assert.Single(_store.Open);
        Assert.Equal(2, _listener.Opened.Count);
    }

    [Fact]
    public async Task Still_judges_a_series_that_has_gone_quiet_so_its_anomaly_can_end()
    {
        AddMinutes(Traffic.Acme, T0.AddMinutes(-60), 60, Normal);
        _store.SetOpen(new OpenAnomaly(
            Guid.NewGuid(), Traffic.Acme, Signal.Volume, T0.AddMinutes(-20), 100, BaselineBasis.Recent, 500, 500, 5, 0, T0.AddMinutes(-1)));
        var detector = Create();

        // No traffic at all in the window.
        await detector.RunAsync(Round(10), CancellationToken.None);
        await detector.RunAsync(Round(11), CancellationToken.None);
        await detector.RunAsync(Round(12), CancellationToken.None);

        Assert.Empty(_store.Open);
        Assert.Single(_listener.Closed);
    }

    [Fact]
    public async Task Series_are_judged_independently()
    {
        var globex = new SeriesKey(TenantId.Parse("globex"), "openai-chat");
        SeedSpendSpike();
        AddMinutes(globex, T0.AddMinutes(-60), 70, Normal);

        await Create().RunAsync(Round(6), CancellationToken.None);

        Assert.All(_store.Open, anomaly => Assert.Equal(Traffic.Acme, anomaly.Series));
        Assert.NotEmpty(_store.Open);
    }

    [Fact]
    public async Task Reuses_what_it_learned_about_normal_until_the_refresh_interval_has_passed()
    {
        SeedSpendSpike();
        var detector = Create();

        await detector.RunAsync(Round(6), CancellationToken.None);
        await detector.RunAsync(Round(7), CancellationToken.None);
        await detector.RunAsync(Round(8), CancellationToken.None);
        Assert.Equal(1, _store.HistoryLoads);

        await detector.RunAsync(Round(17), CancellationToken.None);
        Assert.Equal(2, _store.HistoryLoads);
    }

    [Fact]
    public async Task Learns_again_when_the_hour_of_the_week_changes()
    {
        AddMinutes(Traffic.Acme, T0.AddMinutes(-60), 130, Normal);
        _options.BaselineRefreshInterval = TimeSpan.FromHours(6);
        var detector = Create();

        await detector.RunAsync(T0.AddMinutes(50), CancellationToken.None);
        await detector.RunAsync(T0.AddMinutes(51), CancellationToken.None);
        Assert.Equal(1, _store.HistoryLoads);

        // 03:10 is a different hour of the week.
        await detector.RunAsync(T0.AddMinutes(71), CancellationToken.None);
        Assert.Equal(2, _store.HistoryLoads);
    }

    [Fact]
    public async Task A_listener_that_fails_cannot_stop_detection()
    {
        SeedSpendSpike();

        await Create(new ThrowingAnomalyListener()).RunAsync(Round(6), CancellationToken.None);

        Assert.Single(_store.Open);
        Assert.Single(_listener.Opened);
    }

    private sealed class ThrowingAnomalyListener : IAnomalyListener
    {
        public void AnomalyOpened(SeriesKey series, Finding finding, string description, DateTimeOffset at) =>
            throw new InvalidOperationException("alerting is down");

        public void AnomalyClosed(OpenAnomaly anomaly, DateTimeOffset at) => throw new InvalidOperationException("alerting is down");
    }
}
