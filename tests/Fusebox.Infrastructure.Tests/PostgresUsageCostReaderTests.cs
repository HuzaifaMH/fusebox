using Fusebox.Domain;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fusebox.Infrastructure.Tests;

/// <summary>What Fusebox charged in a period, read back from the per-hour totals.</summary>
[Collection(SharedPostgres.Name)]
public class PostgresUsageCostReaderTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null)
        {
            return;
        }

        await RunAsync("DROP TABLE IF EXISTS requests, rollups_minute, rollups_hour CASCADE");

        // Creates the tables, the way the gateway does at startup.
        var store = new PostgresTelemetryStore(Options(), NullLogger<PostgresTelemetryStore>.Instance);
        _disposables.Add(store);
        await store.DropRawBeforeAsync(DateTimeOffset.UnixEpoch, CancellationToken.None);
        await RunAsync("SELECT 1 FROM rollups_hour LIMIT 1");
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private IOptions<TelemetryOptions> Options() => Microsoft.Extensions.Options.Options.Create(new TelemetryOptions
    {
        Store = TelemetryStoreKind.Postgres,
        Postgres = new Fusebox.Infrastructure.Catalog.PostgresOptions { ConnectionString = postgres.ConnectionString! },
    });

    private async Task RunAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AddHourAsync(string bucket, string tenant, string model, long costMicroUsd)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO rollups_hour (bucket, tenant_id, route_id, model, requests, forwarded, denied, rejected, unavailable, failed, errors,
                                      input_tokens, output_tokens, cost_micro_usd, duration_ms_sum, duration_ms_max, duration_buckets)
            VALUES (@bucket, @tenant, 'openai-chat', @model, 1, 1, 0, 0, 0, 0, 0, 10, 20, @cost, 100, 100, ARRAY[]::bigint[])
            """, connection);
        command.Parameters.AddWithValue("bucket", DateTimeOffset.Parse(bucket, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("model", model);
        command.Parameters.AddWithValue("cost", costMicroUsd);
        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Sums_each_models_cost_across_tenants_and_hours_inside_the_period_only()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await AddHourAsync("2026-03-01T00:00:00Z", "acme", "gpt-a", 1_000_000);
        await AddHourAsync("2026-03-01T00:00:00Z", "globex", "gpt-a", 2_000_000);
        await AddHourAsync("2026-03-01T05:00:00Z", "acme", "gpt-b", 500_000);
        await AddHourAsync("2026-02-28T23:00:00Z", "acme", "gpt-a", 9_000_000);
        await AddHourAsync("2026-03-02T00:00:00Z", "acme", "gpt-a", 9_000_000);

        var reader = new PostgresUsageCostReader(Options());
        _disposables.Add(reader);

        var costs = await reader.CostByModelAsync(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), DateTimeOffset.Parse("2026-03-02T00:00:00Z"), CancellationToken.None);

        Assert.Equal(2, costs.Count);
        Assert.Equal(Money.FromMicroDollars(3_000_000), costs["gpt-a"]);
        Assert.Equal(Money.FromMicroDollars(500_000), costs["gpt-b"]);
    }

    [SkippableFact]
    public async Task A_period_with_no_spend_is_empty()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var reader = new PostgresUsageCostReader(Options());
        _disposables.Add(reader);

        Assert.Empty(await reader.CostByModelAsync(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), DateTimeOffset.Parse("2026-03-02T00:00:00Z"), CancellationToken.None));
    }
}
