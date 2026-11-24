using Fusebox.Application.Detection;
using Fusebox.Infrastructure.Detection;
using Fusebox.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static Fusebox.Infrastructure.Tests.RecordSamples;

namespace Fusebox.Infrastructure.Tests;

public class DetectionOptionsValidatorTests
{
    private static readonly TelemetryOptions PostgresTelemetry = new()
    {
        Store = TelemetryStoreKind.Postgres,
        Postgres = new Catalog.PostgresOptions { ConnectionString = "Host=localhost" },
    };

    private static DetectionOptionsValidator Validator(TelemetryOptions? telemetry = null) => new(Options.Create(telemetry ?? PostgresTelemetry));

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.True(Validator().Validate(null, new DetectionOptions { Enabled = true }).Succeeded);
    }

    [Fact]
    public void Detection_that_is_switched_off_needs_nothing_else()
    {
        Assert.True(Validator(new TelemetryOptions()).Validate(null, new DetectionOptions()).Succeeded);
    }

    [Fact]
    public void Detection_needs_telemetry_in_postgres_because_that_is_where_the_totals_are()
    {
        var result = Validator(new TelemetryOptions()).Validate(null, new DetectionOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains("Telemetry", string.Join(' ', result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lag_must_be_longer_than_the_rollup_interval_so_totals_are_complete_when_judged()
    {
        var options = new DetectionOptions { Enabled = true, Lag = TimeSpan.FromSeconds(30) };

        Assert.True(Validator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(1.5, 2)]
    [InlineData(4, 1)]
    [InlineData(4, 0.5)]
    public void The_clear_ratio_must_sit_between_one_and_the_fire_ratio(double fire, double clear)
    {
        var options = new DetectionOptions { Enabled = true, FireRatio = fire, ClearRatio = clear };

        Assert.True(Validator().Validate(null, options).Failed);
    }

    [Fact]
    public void The_window_cannot_need_more_minutes_with_data_than_it_has()
    {
        var options = new DetectionOptions { Enabled = true, WindowMinutes = 3, MinMinutesWithData = 4 };

        Assert.True(Validator().Validate(null, options).Failed);
    }

    [Fact]
    public void A_recent_baseline_shorter_than_the_samples_it_needs_is_rejected()
    {
        var options = new DetectionOptions { Enabled = true, RecentBaseline = TimeSpan.FromMinutes(10), MinRecentSamples = 20 };

        Assert.True(Validator().Validate(null, options).Failed);
    }
}

public class AnomalyDetectionJobTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-04T12:00:00Z");

    private sealed class ExplodingStore : Application.Detection.IAnomalyStore
    {
        public int Attempts { get; private set; }

        public ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("the database is down");
        }

        public ValueTask<IReadOnlyList<MinuteRow>> LoadMinutesAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<MinuteRow>> LoadHistoryAsync(IReadOnlyCollection<SeriesKey> series, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<OpenAnomaly>> LoadOpenAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyDictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>> LoadRecentlyClosedAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask SaveAsync(AnomalyChange change, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static AnomalyDetectionJob CreateJob(ExplodingStore store, bool enabled)
    {
        var options = Options.Create(new DetectionOptions { Enabled = enabled });
        var detector = new AnomalyDetector(store, [], options, NullLogger<AnomalyDetector>.Instance);

        return new AnomalyDetectionJob(detector, options, new FakeTimeProvider(Now), NullLogger<AnomalyDetectionJob>.Instance);
    }

    [Fact]
    public async Task A_failing_round_is_survived_so_the_next_one_can_run()
    {
        var store = new ExplodingStore();
        var job = CreateJob(store, enabled: true);

        await job.TickAsync(CancellationToken.None);
        await job.TickAsync(CancellationToken.None);

        Assert.Equal(2, store.Attempts);
    }

    [Fact]
    public async Task Does_nothing_when_detection_is_switched_off()
    {
        var store = new ExplodingStore();
        var job = CreateJob(store, enabled: false);

        await job.StartAsync(CancellationToken.None);
        await job.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.Attempts);
    }
}
