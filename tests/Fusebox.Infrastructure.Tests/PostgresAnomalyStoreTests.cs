using Fusebox.Application.Detection;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Detection.Postgres;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Fusebox.Infrastructure.Tests.RecordSamples;

namespace Fusebox.Infrastructure.Tests;

/// <summary>The anomaly store, and the whole chain from raw request records to a saved anomaly, on a real PostgreSQL.</summary>
[Collection(SharedPostgres.Name)]
public class PostgresAnomalyStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-04T02:00:00Z");
    private static readonly SeriesKey Acme = new(TenantId.Parse("acme"), "openai-chat");

    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is not null)
        {
            await RunAsync("DROP TABLE IF EXISTS requests, rollups_minute, rollups_hour, anomalies CASCADE");
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private IOptions<TelemetryOptions> TelemetryOptions() => Options.Create(new TelemetryOptions
    {
        Store = TelemetryStoreKind.Postgres,
        Postgres = new PostgresOptions { ConnectionString = postgres.ConnectionString! },
    });

    private PostgresAnomalyStore CreateStore()
    {
        var store = new PostgresAnomalyStore(TelemetryOptions());
        _disposables.Add(store);
        return store;
    }

    private PostgresTelemetryStore CreateTelemetryStore()
    {
        var store = new PostgresTelemetryStore(TelemetryOptions(), NullLogger<PostgresTelemetryStore>.Instance);
        _disposables.Add(store);
        return store;
    }

    private async Task RunAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static OpenAnomaly Anomaly(Signal signal = Signal.Spend, int calm = 0) => new(
        Guid.NewGuid(), Acme, signal, T0, 200_000, BaselineBasis.Recent, 1_000_000, 1_000_000, 5, calm, T0);

    /// <summary>Records for a minute: <paramref name="count"/> forwarded calls costing <paramref name="costMicroUsd"/> each.</summary>
    private static IEnumerable<RequestRecord> Minute(DateTimeOffset minute, int count, long costMicroUsd, string model = "m") =>
        Enumerable.Range(0, count).Select(i => Record(at: minute.AddMilliseconds(i * 500), model: model, costMicroUsd: costMicroUsd, durationMs: 100 + (i % 400)));

    [SkippableFact]
    public async Task Reads_the_minute_totals_the_telemetry_pipeline_keeps()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var telemetry = CreateTelemetryStore();
        await telemetry.WriteAsync([.. Minute(T0, 10, 2_000), .. Minute(T0.AddMinutes(1), 4, 2_000, model: "other")], CancellationToken.None);
        await telemetry.RollUpAsync(RollupGranularity.Minute, T0, T0.AddMinutes(2), CancellationToken.None);

        var rows = await CreateStore().LoadMinutesAsync(T0, T0.AddMinutes(1), CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(Acme, row.Series);
        Assert.Equal(T0, row.Stats.Bucket);
        Assert.Equal(10, row.Stats.Requests);
        Assert.Equal(10, row.Stats.Forwarded);
        Assert.Equal(20_000, row.Stats.CostMicroUsd);
        Assert.Equal(LatencyBuckets.Count, row.Stats.LatencyBuckets.Count);
        Assert.Equal(10, row.Stats.LatencyBuckets.Sum());
    }

    [SkippableFact]
    public async Task Reads_one_row_per_model_when_a_minute_has_several()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var telemetry = CreateTelemetryStore();
        await telemetry.WriteAsync([.. Minute(T0, 6, 2_000, "a"), .. Minute(T0, 4, 2_000, "b")], CancellationToken.None);
        await telemetry.RollUpAsync(RollupGranularity.Minute, T0, T0.AddMinutes(1), CancellationToken.None);

        var rows = await CreateStore().LoadMinutesAsync(T0, T0.AddMinutes(1), CancellationToken.None);

        // The detector adds them up; the store returns what was stored.
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, MinuteStats.Merge(rows.Select(r => r.Stats)).Requests);
    }

    [SkippableFact]
    public async Task Reads_history_for_the_given_series_over_the_given_ranges_only()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var telemetry = CreateTelemetryStore();
        var globexRecords = Minute(T0, 5, 2_000).Select(r => r with { TenantId = TenantId.Parse("globex") });
        await telemetry.WriteAsync(
            [.. Minute(T0, 5, 2_000), .. Minute(T0.AddHours(1), 5, 2_000), .. Minute(T0.AddHours(3), 5, 2_000), .. globexRecords],
            CancellationToken.None);
        await telemetry.RollUpAsync(RollupGranularity.Minute, T0, T0.AddHours(4), CancellationToken.None);

        var rows = await CreateStore().LoadHistoryAsync(
            [Acme],
            [(T0, T0.AddMinutes(30)), (T0.AddHours(3), T0.AddHours(3).AddMinutes(30))],
            CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(Acme, row.Series));
        Assert.Equal([T0, T0.AddHours(3)], rows.Select(r => r.Stats.Bucket).Order());
    }

    [SkippableFact]
    public async Task Reading_history_for_no_series_or_no_ranges_reads_nothing()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();

        Assert.Empty(await store.LoadHistoryAsync([], [(T0, T0.AddHours(1))], CancellationToken.None));
        Assert.Empty(await store.LoadHistoryAsync([Acme], [], CancellationToken.None));
    }

    [SkippableFact]
    public async Task Creates_the_tables_it_needs_itself()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);

        Assert.Empty(await CreateStore().LoadMinutesAsync(T0, T0.AddHours(1), CancellationToken.None));
        Assert.Empty(await CreateStore().LoadOpenAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Keeps_an_anomaly_from_start_to_finish()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var anomaly = Anomaly();

        await store.SaveAsync(new AnomalyOpenedChange(anomaly), CancellationToken.None);
        var open = Assert.Single(await store.LoadOpenAsync(CancellationToken.None));
        Assert.Equal(anomaly, open);

        await store.SaveAsync(new AnomalyProgressedChange(anomaly.Id, 2_000_000, 10, 0, T0.AddMinutes(1)), CancellationToken.None);
        open = Assert.Single(await store.LoadOpenAsync(CancellationToken.None));
        Assert.Equal(2_000_000, open.PeakValue);
        Assert.Equal(10, open.PeakRatio);
        Assert.Equal(T0.AddMinutes(1), open.LastSeenAt);

        await store.SaveAsync(new AnomalyProgressedChange(anomaly.Id, 2_000_000, 10, 2, T0.AddMinutes(2)), CancellationToken.None);
        Assert.Equal(2, Assert.Single(await store.LoadOpenAsync(CancellationToken.None)).CalmEvaluations);

        await store.SaveAsync(new AnomalyClosedChange(anomaly.Id, T0.AddMinutes(3)), CancellationToken.None);
        Assert.Empty(await store.LoadOpenAsync(CancellationToken.None));
        Assert.Equal("closed", await ScalarAsync<string>("SELECT state FROM anomalies"));
    }

    [SkippableFact]
    public async Task Reports_when_each_signal_last_had_an_anomaly_end_within_the_period_asked_about()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var recent = Anomaly(Signal.Spend);
        var old = Anomaly(Signal.Volume);
        await store.SaveAsync(new AnomalyOpenedChange(recent), CancellationToken.None);
        await store.SaveAsync(new AnomalyOpenedChange(old), CancellationToken.None);
        await store.SaveAsync(new AnomalyClosedChange(recent.Id, T0.AddMinutes(50)), CancellationToken.None);
        await store.SaveAsync(new AnomalyClosedChange(old.Id, T0.AddMinutes(5)), CancellationToken.None);

        var closed = await store.LoadRecentlyClosedAsync(T0.AddMinutes(30), CancellationToken.None);

        var only = Assert.Single(closed);
        Assert.Equal((Acme, Signal.Spend), only.Key);
        Assert.Equal(T0.AddMinutes(50), only.Value);
    }

    [SkippableFact]
    public async Task The_database_allows_only_one_open_anomaly_per_series_and_signal()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await store.SaveAsync(new AnomalyOpenedChange(Anomaly(Signal.Spend)), CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await store.SaveAsync(new AnomalyOpenedChange(Anomaly(Signal.Spend)), CancellationToken.None));
        await store.SaveAsync(new AnomalyOpenedChange(Anomaly(Signal.Volume)), CancellationToken.None);
    }

    [SkippableFact]
    public async Task Lets_only_one_gateway_instance_run_detection_at_a_time()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var first = CreateStore();
        var second = CreateStore();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var running = first.TryRunExclusivelyAsync(
            async _ =>
            {
                started.SetResult();
                await release.Task;
            },
            CancellationToken.None).AsTask();
        await started.Task;

        var secondRan = false;
        var refused = !await second.TryRunExclusivelyAsync(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        release.SetResult();
        Assert.True(await running);
        Assert.True(refused);
        Assert.False(secondRan);

        // Once the first has finished, the second may run.
        Assert.True(await second.TryRunExclusivelyAsync(_ => Task.CompletedTask, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Releases_the_lock_even_when_the_work_fails()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.TryRunExclusivelyAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.True(await store.TryRunExclusivelyAsync(_ => Task.CompletedTask, CancellationToken.None));
    }

    [SkippableFact]
    public async Task From_raw_request_records_to_an_anomaly_and_back_to_normal()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);

        // 45 minutes of normal traffic ($0.20 a minute), five minutes at five times the cost per call, then normal again.
        var telemetry = CreateTelemetryStore();
        var records = new List<RequestRecord>();
        for (var minute = -45; minute < 15; minute++)
        {
            records.AddRange(Minute(T0.AddMinutes(minute), 100, minute is >= 0 and < 5 ? 10_000 : 2_000));
        }

        foreach (var batch in records.Chunk(1_000))
        {
            await telemetry.WriteAsync(batch, CancellationToken.None);
        }

        await telemetry.RollUpAsync(RollupGranularity.Minute, T0.AddHours(-1), T0.AddMinutes(20), CancellationToken.None);

        var anomalies = CreateStore();
        var detector = new AnomalyDetector(
            anomalies,
            [],
            Options.Create(new DetectionOptions { Enabled = true }),
            NullLogger<AnomalyDetector>.Instance);

        await detector.RunAsync(T0.AddMinutes(6), CancellationToken.None);

        var open = Assert.Single(await anomalies.LoadOpenAsync(CancellationToken.None));
        Assert.Equal(Signal.Spend, open.Signal);
        Assert.Equal(Acme, open.Series);
        Assert.InRange(open.PeakRatio, 4.5, 5.5);
        Assert.Equal(BaselineBasis.Recent, open.Basis);

        foreach (var minute in new[] { 7, 8, 9, 10, 11, 12 })
        {
            await detector.RunAsync(T0.AddMinutes(minute), CancellationToken.None);
        }

        Assert.Empty(await anomalies.LoadOpenAsync(CancellationToken.None));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM anomalies WHERE state = 'closed'"));
    }
}
