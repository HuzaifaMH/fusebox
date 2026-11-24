using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Catalog.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;
using static Fusebox.Infrastructure.Tests.CatalogSamples;

namespace Fusebox.Infrastructure.Tests;

/// <summary>
/// Starts a real PostgreSQL in Docker, so the schema and queries are tested against the real thing.
/// If Docker is not running the tests are skipped, not failed.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? ConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

#pragma warning disable CA1031 // Any failure to start Docker means "skip", whatever the cause.
    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            UnavailableReason = $"Docker and PostgreSQL are not available: {exception.GetType().Name}";
        }
    }
#pragma warning restore CA1031

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Lets every test class that needs PostgreSQL share one container instead of starting its own.</summary>
[CollectionDefinition(Name)]
public sealed class SharedPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

[Collection(SharedPostgres.Name)]
public class PostgresCatalogSourceTests(PostgresFixture postgres) : IAsyncLifetime
{
    // A key hash for a test tenant: any 64 lowercase hex characters.
    private static readonly string HashOne = new('1', 64);
    private static readonly string HashTwo = new('2', 64);

    private readonly List<PostgresCatalogSource> _sources = [];

    // Every test starts from empty tables.
    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null)
        {
            return;
        }

        await RunAsync("DROP TABLE IF EXISTS tenant_model_prices, model_prices, tenants");
        await CreateSource().PrepareAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        foreach (var source in _sources)
        {
            await source.DisposeAsync();
        }
    }

    private PostgresCatalogSource CreateSource(bool applySchema = true)
    {
        var source = new PostgresCatalogSource(
            Options.Create(new CatalogSourceOptions
            {
                Kind = CatalogSourceKind.Postgres,
                Postgres = new PostgresOptions { ConnectionString = postgres.ConnectionString!, ApplySchemaOnStartup = applySchema },
            }),
            NullLogger<PostgresCatalogSource>.Instance);

        _sources.Add(source);
        return source;
    }

    private async Task RunAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private Task AddTenantAsync(string id, string hash, long limitMicroUsd = 10_000_000, int windowSeconds = 3600, bool enabled = true) =>
        RunAsync(
            "INSERT INTO tenants (id, name, api_key_hash, budget_limit_micro_usd, budget_window_seconds, enabled) VALUES (@id, @name, @hash, @limit, @window, @enabled)",
            ("id", id),
            ("name", $"Tenant {id}"),
            ("hash", hash),
            ("limit", limitMicroUsd),
            ("window", windowSeconds),
            ("enabled", enabled));

    private Task AddPriceAsync(string model, long inputMicroUsd, long outputMicroUsd, string effectiveFrom) =>
        RunAsync(
            "INSERT INTO model_prices (model, effective_from, input_micro_usd_per_million, output_micro_usd_per_million) VALUES (@model, @from::timestamptz, @input, @output)",
            ("model", model),
            ("from", effectiveFrom),
            ("input", inputMicroUsd),
            ("output", outputMicroUsd));

    private async Task<CatalogData> LoadAsync()
    {
        await using var source = CreateSource();
        return await source.LoadAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Loads_a_tenant_with_its_budget_and_key_hash()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await AddTenantAsync("acme", HashOne, limitMicroUsd: 12_500_000, windowSeconds: 7200);

        var data = await LoadAsync();

        var entry = Assert.Single(data.Tenants);
        Assert.Equal(HashOne, entry.ApiKeyHash);
        Assert.Equal(TenantId.Parse("acme"), entry.Tenant.Id);
        Assert.Equal("Tenant acme", entry.Tenant.Name);
        Assert.Equal(Money.FromDollars(12.5m), entry.Tenant.Budget.Limit);
        Assert.Equal(TimeSpan.FromHours(2), entry.Tenant.Budget.Window);
    }

    [SkippableFact]
    public async Task Does_not_load_a_disabled_tenant()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await AddTenantAsync("active", HashOne);
        await AddTenantAsync("paused", HashTwo, enabled: false);

        var data = await LoadAsync();

        Assert.Equal(TenantId.Parse("active"), Assert.Single(data.Tenants).Tenant.Id);
    }

    [SkippableFact]
    public async Task Loads_the_newest_price_already_in_effect_for_each_model()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await AddPriceAsync("gpt", 100_000, 200_000, "2020-01-01T00:00:00Z");
        await AddPriceAsync("gpt", 150_000, 600_000, "2024-01-01T00:00:00Z");
        await AddPriceAsync("gpt", 999_000, 999_000, "2999-01-01T00:00:00Z");
        await AddPriceAsync("other", 1_000_000, 2_000_000, "2020-01-01T00:00:00Z");

        var data = await LoadAsync();

        var gpt = data.Prices.Single(p => p.Model == "gpt").Price;
        Assert.Equal(Money.FromMicroDollars(150_000), gpt.InputPerMillionTokens);
        Assert.Equal(Money.FromMicroDollars(600_000), gpt.OutputPerMillionTokens);
        Assert.Equal(2, data.Prices.Count);
    }

    [SkippableFact]
    public async Task Does_not_load_a_price_that_is_not_in_effect_yet()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await AddPriceAsync("future-model", 1, 1, "2999-01-01T00:00:00Z");

        Assert.Empty((await LoadAsync()).Prices);
    }

    [SkippableFact]
    public async Task Preparing_the_schema_twice_changes_nothing()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await AddTenantAsync("acme", HashOne);

        await CreateSource().PrepareAsync(CancellationToken.None);

        Assert.Single((await LoadAsync()).Tenants);
    }

    [SkippableFact]
    public async Task Several_gateway_instances_can_prepare_the_schema_at_the_same_time()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await RunAsync("DROP TABLE IF EXISTS tenant_model_prices, model_prices, tenants");

        var starts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => CreateSource().PrepareAsync(CancellationToken.None).AsTask()));
        await Task.WhenAll(starts);

        Assert.Empty((await LoadAsync()).Tenants);
    }

    [SkippableFact]
    public async Task Leaves_the_schema_alone_when_told_not_to_manage_it()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await RunAsync("DROP TABLE IF EXISTS tenant_model_prices, model_prices, tenants");

        await CreateSource(applySchema: false).PrepareAsync(CancellationToken.None);

        await Assert.ThrowsAsync<PostgresException>(() => LoadAsync());
    }

    [SkippableFact]
    public async Task Skips_a_row_that_is_not_a_valid_tenant_without_losing_the_others()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);

        // The table's own rules normally keep such a row out, so lift one to prove the loader defends itself too.
        await RunAsync("ALTER TABLE tenants DROP CONSTRAINT tenants_id_check");
        await AddTenantAsync("good", HashOne);
        await AddTenantAsync("not a valid id!", HashTwo);

        var data = await LoadAsync();

        Assert.Equal(TenantId.Parse("good"), Assert.Single(data.Tenants).Tenant.Id);
    }

    [SkippableFact]
    public async Task The_database_itself_refuses_a_bad_budget_or_key_hash()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);

        await Assert.ThrowsAsync<PostgresException>(() => AddTenantAsync("zero", HashOne, limitMicroUsd: 0));
        await Assert.ThrowsAsync<PostgresException>(() => AddTenantAsync("short", HashOne, windowSeconds: 30));
        await Assert.ThrowsAsync<PostgresException>(() => AddTenantAsync("badhash", "NOT-HEX"));
        await AddTenantAsync("dupe", HashOne);
        await Assert.ThrowsAsync<PostgresException>(() => AddTenantAsync("dupe2", HashOne));
    }

    [SkippableFact]
    public async Task A_change_in_the_database_reaches_the_gateway_on_the_next_reload()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        await AddTenantAsync("acme", HashOne, limitMicroUsd: 10_000_000);

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-04T10:15:00Z"));
        var snapshots = new CatalogSnapshotProvider();
        var refresher = new CatalogRefresher(
            CreateSource(),
            new NoCatalogCache(),
            snapshots,
            Options.Create(new CatalogSourceOptions { Kind = CatalogSourceKind.Postgres }),
            time,
            NullLogger<CatalogRefresher>.Instance);
        await refresher.InitializeAsync(CancellationToken.None);
        Assert.Equal(Money.FromDollars(10m), snapshots.Current.TenantsByKeyHash[HashOne].Budget.Limit);

        await RunAsync("UPDATE tenants SET budget_limit_micro_usd = 50000000 WHERE id = 'acme'");
        await AddTenantAsync("globex", HashTwo);
        time.Advance(TimeSpan.FromMinutes(5));
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal(Money.FromDollars(50m), snapshots.Current.TenantsByKeyHash[HashOne].Budget.Limit);
        Assert.Equal(2, snapshots.Current.TenantsByKeyHash.Count);
    }
}
