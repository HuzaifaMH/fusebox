using Fusebox.Application.Budgeting;
using Fusebox.Application.Telemetry;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using static Fusebox.Infrastructure.Tests.RecordSamples;

namespace Fusebox.Infrastructure.Tests;

/// <summary>The telemetry store against a real PostgreSQL: the partitions, the batch save and the rollups.</summary>
[Collection(SharedPostgres.Name)]
public class PostgresTelemetryStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset NextDay = Noon.AddDays(1);

    private readonly List<PostgresTelemetryStore> _stores = [];

    // Every test starts from empty tables.
    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is not null)
        {
            await RunAsync("DROP TABLE IF EXISTS requests, rollups_minute, rollups_hour CASCADE");
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            await store.DisposeAsync();
        }
    }

    private PostgresTelemetryStore CreateStore()
    {
        var store = new PostgresTelemetryStore(
            Options.Create(new TelemetryOptions
            {
                Store = TelemetryStoreKind.Postgres,
                Postgres = new PostgresOptions { ConnectionString = postgres.ConnectionString! },
            }),
            NullLogger<PostgresTelemetryStore>.Instance);

        _stores.Add(store);
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

    private static Task WriteAsync(PostgresTelemetryStore store, params RequestRecord[] records) =>
        store.WriteAsync(records, CancellationToken.None).AsTask();

    private static async Task RollUpMinuteAsync(PostgresTelemetryStore store, DateTimeOffset from, DateTimeOffset to) =>
        Assert.True(await store.RollUpAsync(RollupGranularity.Minute, from, to, CancellationToken.None));

    private static async Task<NpgsqlDataReader> QueryAsync(NpgsqlConnection connection, string sql)
    {
        await connection.OpenAsync();
        var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteReaderAsync();
    }

    [SkippableFact]
    public async Task Saves_a_record_with_every_field()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var record = Record(
            tenant: "acme", route: "openai-chat", model: "gpt", disposition: RequestDisposition.Forwarded,
            status: 200, durationMs: 1_234, streaming: true, costMicroUsd: 50_000, reservedMicroUsd: 201_000);

        await WriteAsync(CreateStore(), record);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await using var reader = await QueryAsync(connection, "SELECT id, started_at, tenant_id, route_id, model, disposition, denial_reason, status_code, duration_ms, is_streaming, input_tokens, output_tokens, cost_micro_usd, reserved_micro_usd FROM requests");
        Assert.True(await reader.ReadAsync());
        Assert.Equal(record.Id, reader.GetGuid(0));
        Assert.Equal(record.StartedAt, new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero));
        Assert.Equal("acme", reader.GetString(2));
        Assert.Equal("openai-chat", reader.GetString(3));
        Assert.Equal("gpt", reader.GetString(4));
        Assert.Equal("forwarded", reader.GetString(5));
        Assert.True(await reader.IsDBNullAsync(6));
        Assert.Equal(200, reader.GetInt16(7));
        Assert.Equal(1_234, reader.GetInt32(8));
        Assert.True(reader.GetBoolean(9));
        Assert.Equal(10, reader.GetInt64(10));
        Assert.Equal(20, reader.GetInt64(11));
        Assert.Equal(50_000, reader.GetInt64(12));
        Assert.Equal(201_000, reader.GetInt64(13));
    }

    [SkippableFact]
    public async Task Saves_what_is_not_known_as_null()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var record = Record(model: null, disposition: RequestDisposition.Denied, denial: DenialReason.BudgetExceeded, status: 429, noUsage: true, costMicroUsd: 0, reservedMicroUsd: 0);

        await WriteAsync(CreateStore(), record);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await using var reader = await QueryAsync(connection, "SELECT model, denial_reason, input_tokens, output_tokens FROM requests");
        Assert.True(await reader.ReadAsync());
        Assert.True(await reader.IsDBNullAsync(0));
        Assert.Equal("budget_exceeded", reader.GetString(1));
        Assert.True(await reader.IsDBNullAsync(2));
        Assert.True(await reader.IsDBNullAsync(3));
    }

    [SkippableFact]
    public async Task Saving_the_same_batch_twice_keeps_one_row_per_record()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var batch = new[] { Record(), Record(), Record() };

        await WriteAsync(store, batch);
        await WriteAsync(store, batch);

        Assert.Equal(3, await ScalarAsync<long>("SELECT count(*) FROM requests"));
    }

    [SkippableFact]
    public async Task Creates_a_partition_for_each_day_in_a_batch()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);

        await WriteAsync(CreateStore(), Record(at: Noon), Record(at: NextDay), Record(at: Noon.AddMinutes(1)));

        var partitions = await ScalarAsync<long>("SELECT count(*) FROM pg_inherits WHERE inhparent = 'requests'::regclass");
        Assert.Equal(2, partitions);
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM requests_20260304"));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM requests_20260305"));
    }

    [SkippableFact]
    public async Task Writing_an_empty_batch_does_nothing()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);

        await CreateStore().WriteAsync([], CancellationToken.None);
    }

    [SkippableFact]
    public async Task Several_gateway_instances_can_start_a_new_day_at_the_same_time()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);

        var writes = Enumerable.Range(0, 6).Select(_ => Task.Run(() => WriteAsync(CreateStore(), Record(), Record())));
        await Task.WhenAll(writes);

        Assert.Equal(12, await ScalarAsync<long>("SELECT count(*) FROM requests"));
    }

    [SkippableFact]
    public async Task Totals_a_minute_by_tenant_route_and_model()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(
            store,
            Record(durationMs: 100, status: 200, costMicroUsd: 50_000),
            Record(durationMs: 300, status: 200, costMicroUsd: 70_000),
            Record(durationMs: 700, status: 500, costMicroUsd: 0, noUsage: true),
            Record(disposition: RequestDisposition.Denied, denial: DenialReason.BudgetExceeded, status: 429, durationMs: 1, costMicroUsd: 0, noUsage: true),
            Record(disposition: RequestDisposition.Rejected, model: null, status: 400, durationMs: 1, costMicroUsd: 0, noUsage: true),
            Record(tenant: "globex", costMicroUsd: 10_000));

        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await using var reader = await QueryAsync(connection, "SELECT tenant_id, model, requests, forwarded, denied, rejected, unavailable, failed, errors, input_tokens, output_tokens, cost_micro_usd, duration_ms_sum, duration_ms_max FROM rollups_minute WHERE tenant_id = 'acme' AND model = 'm'");
        Assert.True(await reader.ReadAsync());
        Assert.Equal(4, reader.GetInt64(2)); // the rejected call has no model, so it is counted in its own row
        Assert.Equal(3, reader.GetInt64(3)); // forwarded
        Assert.Equal(1, reader.GetInt64(4)); // denied
        Assert.Equal(0, reader.GetInt64(5)); // rejected (that one has no model, so it is in its own row)
        Assert.Equal(1, reader.GetInt64(8)); // the vendor's 500
        Assert.Equal(20, reader.GetInt64(9)); // tokens of the two calls that reported usage
        Assert.Equal(40, reader.GetInt64(10));
        Assert.Equal(120_000, reader.GetInt64(11));
        Assert.Equal(1_100, reader.GetInt64(12)); // latency counts forwarded calls only: 100 + 300 + 700
        Assert.Equal(700, reader.GetInt32(13));
        await reader.CloseAsync();

        Assert.Equal(1, await ScalarAsync<long>("SELECT rejected FROM rollups_minute WHERE tenant_id = 'acme' AND model = ''"));
        Assert.Equal(3, await ScalarAsync<long>("SELECT count(*) FROM rollups_minute"));
    }

    [SkippableFact]
    public async Task Keeps_a_latency_histogram_of_forwarded_calls_that_percentiles_can_be_read_from()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();

        // Bounds are 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000 ms, then open-ended.
        await WriteAsync(
            store,
            Record(durationMs: 10),
            Record(durationMs: 50),
            Record(durationMs: 51),
            Record(durationMs: 250),
            Record(durationMs: 30_000),
            Record(durationMs: 30_001),
            Record(durationMs: 99_999),
            Record(disposition: RequestDisposition.Denied, durationMs: 5, status: 429));

        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));

        var histogram = await ScalarAsync<long[]>("SELECT duration_buckets FROM rollups_minute");
        Assert.Equal(LatencyBuckets.Count, histogram.Length);
        Assert.Equal([2, 1, 1, 0, 0, 0, 0, 0, 1, 2], histogram);
    }

    [SkippableFact]
    public async Task Running_the_totals_again_does_not_count_anything_twice()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(store, Record(costMicroUsd: 50_000), Record(costMicroUsd: 50_000));

        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));
        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));
        await RollUpMinuteAsync(store, Noon.AddMinutes(-10), Noon.AddMinutes(10));

        Assert.Equal(2, await ScalarAsync<long>("SELECT requests FROM rollups_minute"));
        Assert.Equal(100_000, await ScalarAsync<long>("SELECT cost_micro_usd FROM rollups_minute"));
    }

    [SkippableFact]
    public async Task A_record_that_arrives_late_is_counted_by_the_next_run()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(store, Record());
        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));

        await WriteAsync(store, Record(at: Noon.AddSeconds(-5)));
        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));

        Assert.Equal(2, await ScalarAsync<long>("SELECT requests FROM rollups_minute"));
    }

    [SkippableFact]
    public async Task Only_touches_the_buckets_that_overlap_the_window()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(store, Record(at: Noon), Record(at: Noon.AddMinutes(10)));

        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));

        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM rollups_minute"));
    }

    [SkippableFact]
    public async Task Counts_repeated_calls_that_reached_the_vendor_and_ignores_refused_ones()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(
            store,
            Record(at: Noon, repeat: false),
            Record(at: Noon, repeat: true),
            Record(at: Noon, repeat: true),
            Record(at: Noon, repeat: true, disposition: RequestDisposition.Denied, denial: DenialReason.BudgetExceeded, costMicroUsd: 0));

        await RollUpMinuteAsync(store, Noon, Noon.AddSeconds(1));
        Assert.True(await store.RollUpAsync(RollupGranularity.Hour, Noon, Noon.AddSeconds(1), CancellationToken.None));

        Assert.Equal(2, await ScalarAsync<long>("SELECT repeats FROM rollups_minute"));
        Assert.Equal(2, await ScalarAsync<long>("SELECT repeats FROM rollups_hour"));
        Assert.Equal(3, await ScalarAsync<long>("SELECT count(*) FROM requests WHERE is_repeat"));
    }

    [SkippableFact]
    public async Task Totals_an_hour_across_its_minutes()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(store, Record(at: Noon), Record(at: Noon.AddMinutes(20)), Record(at: Noon.AddMinutes(40)), Record(at: Noon.AddHours(2)));

        Assert.True(await store.RollUpAsync(RollupGranularity.Hour, Noon, Noon.AddSeconds(1), CancellationToken.None));

        Assert.Equal(3, await ScalarAsync<long>("SELECT requests FROM rollups_hour WHERE bucket = '2026-03-04T12:00:00Z'"));
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM rollups_hour"));
    }

    [SkippableFact]
    public async Task Leaves_the_totals_to_another_instance_that_is_already_computing_them()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(store, Record());

        // Another session holds the lock for per-minute totals. The key is the store's base key plus the granularity.
        await using var other = new NpgsqlConnection(postgres.ConnectionString);
        await other.OpenAsync();
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(7461100)", other))
        {
            await hold.ExecuteNonQueryAsync();
        }

        var minute = await store.RollUpAsync(RollupGranularity.Minute, Noon, Noon.AddSeconds(1), CancellationToken.None);
        var hour = await store.RollUpAsync(RollupGranularity.Hour, Noon, Noon.AddSeconds(1), CancellationToken.None);

        Assert.False(minute);
        Assert.True(hour);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM rollups_minute"));
    }

    [SkippableFact]
    public async Task Drops_whole_days_before_the_cutoff_and_keeps_the_rest()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await WriteAsync(store, Record(at: Noon.AddDays(-3)), Record(at: Noon.AddDays(-2)), Record(at: Noon), Record(at: NextDay));

        var dropped = await store.DropRawBeforeAsync(Noon.AddDays(-1), CancellationToken.None);

        Assert.Equal(2, dropped);
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM requests"));
        Assert.Equal(2, await ScalarAsync<long>("SELECT count(*) FROM pg_inherits WHERE inhparent = 'requests'::regclass"));
    }

    [SkippableFact]
    public async Task Dropping_old_records_leaves_the_totals_made_from_them()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var old = Noon.AddDays(-40);
        await WriteAsync(store, Record(at: old, costMicroUsd: 50_000));
        await RollUpMinuteAsync(store, old, old.AddSeconds(1));

        await store.DropRawBeforeAsync(Noon.AddDays(-30), CancellationToken.None);

        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM requests"));
        Assert.Equal(50_000, await ScalarAsync<long>("SELECT cost_micro_usd FROM rollups_minute"));
    }

    [SkippableFact]
    public async Task The_whole_pipeline_saves_every_queued_record_by_the_time_it_has_stopped()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var options = Options.Create(new TelemetryOptions
        {
            Store = TelemetryStoreKind.Postgres,
            QueueCapacity = 1_000,
            MaxBatchSize = 10,
            Postgres = new PostgresOptions { ConnectionString = postgres.ConnectionString! },
        });
        var meterFactory = new TestMeterFactory();
        using var metrics = new TelemetryMetrics(meterFactory);
        using var meter = new MeterCollector(meterFactory);
        var sink = new ChannelTelemetrySink(options, metrics);
        using var writer = new TelemetryWriter(sink, CreateStore(), options, metrics, TimeProvider.System, NullLogger<TelemetryWriter>.Instance);

        await writer.StartAsync(CancellationToken.None);
        for (var i = 0; i < 25; i++)
        {
            sink.TryRecord(Record(at: DateTimeOffset.UtcNow));
        }

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(25, await ScalarAsync<long>("SELECT count(*) FROM requests"));
        Assert.Equal(25, meter.Total("fusebox.telemetry.written"));
    }
}
