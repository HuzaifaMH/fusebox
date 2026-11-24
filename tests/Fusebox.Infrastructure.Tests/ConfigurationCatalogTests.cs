using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Infrastructure.Tests;

public class ConfigurationCatalogTests
{
    private static readonly string KeyHash = new Sha256ApiKeyHasher().Hash("acme-key");

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-03-04T10:15:00Z"));
    private readonly CatalogSnapshotProvider _snapshots = new();

    private static CatalogOptions Options(decimal budget = 10m) => new()
    {
        Tenants =
        {
            ["acme"] = new TenantOptions
            {
                Name = "Acme",
                ApiKeyHash = KeyHash,
                BudgetLimitUsd = budget,
                BudgetWindow = TimeSpan.FromHours(1),
            },
        },
        Models = { ["m"] = new ModelPriceOptions { InputUsdPerMillionTokens = 1, OutputUsdPerMillionTokens = 2 } },
    };

    private async Task<(SnapshotTenantDirectory Directory, SnapshotPriceCatalog Prices, CatalogRefresher Refresher, StubOptionsMonitor<CatalogOptions> Monitor)> StartAsync(decimal budget = 10m)
    {
        var monitor = new StubOptionsMonitor<CatalogOptions>(Options(budget));
        var refresher = new CatalogRefresher(
            new ConfigurationCatalogSource(monitor),
            new NoCatalogCache(),
            _snapshots,
            Microsoft.Extensions.Options.Options.Create(new CatalogSourceOptions()),
            _time,
            NullLogger<CatalogRefresher>.Instance);

        await refresher.InitializeAsync(CancellationToken.None);
        return (new SnapshotTenantDirectory(_snapshots), new SnapshotPriceCatalog(_snapshots), refresher, monitor);
    }

    [Fact]
    public async Task Finds_a_tenant_by_its_key_hash_with_its_budget()
    {
        var (directory, _, _, _) = await StartAsync();

        var tenant = await directory.FindByApiKeyHashAsync(KeyHash, CancellationToken.None);

        Assert.NotNull(tenant);
        Assert.Equal(TenantId.Parse("acme"), tenant.Id);
        Assert.Equal(Money.FromDollars(10m), tenant.Budget.Limit);
        Assert.Equal(TimeSpan.FromHours(1), tenant.Budget.Window);
    }

    [Fact]
    public async Task An_unknown_hash_finds_nobody()
    {
        var (directory, _, _, _) = await StartAsync();

        Assert.Null(await directory.FindByApiKeyHashAsync(new string('0', 64), CancellationToken.None));
    }

    [Fact]
    public async Task Finds_a_model_price_in_micro_dollars()
    {
        var (_, prices, _, _) = await StartAsync();

        var price = await prices.FindAsync(TenantId.Parse("acme"), "m", CancellationToken.None);

        Assert.NotNull(price);
        Assert.Equal(Money.FromDollars(1m), price.InputPerMillionTokens);
        Assert.Equal(Money.FromDollars(2m), price.OutputPerMillionTokens);
        Assert.Null(await prices.FindAsync(TenantId.Parse("acme"), "other", CancellationToken.None));
    }

    [Fact]
    public async Task Picks_up_changed_configuration_on_the_next_reload()
    {
        var (directory, _, refresher, monitor) = await StartAsync(budget: 10m);
        Assert.Equal(Money.FromDollars(10m), (await directory.FindByApiKeyHashAsync(KeyHash, CancellationToken.None))!.Budget.Limit);

        monitor.CurrentValue = Options(budget: 25m);
        _time.Advance(TimeSpan.FromMinutes(5));
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal(Money.FromDollars(25m), (await directory.FindByApiKeyHashAsync(KeyHash, CancellationToken.None))!.Budget.Limit);
    }

    [Fact]
    public async Task Asking_for_a_tenant_before_any_catalog_is_loaded_is_an_error_not_a_miss()
    {
        var directory = new SnapshotTenantDirectory(_snapshots);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await directory.FindByApiKeyHashAsync(KeyHash, CancellationToken.None));
    }
}

public class CatalogSourceOptionsValidatorTests
{
    private readonly CatalogSourceOptionsValidator _validator = new();

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.True(_validator.Validate(null, new CatalogSourceOptions()).Succeeded);
    }

    [Fact]
    public void Postgres_needs_a_connection_string()
    {
        Assert.True(_validator.Validate(null, new CatalogSourceOptions { Kind = CatalogSourceKind.Postgres }).Failed);
    }

    [Fact]
    public void Postgres_with_a_connection_string_is_valid()
    {
        var options = new CatalogSourceOptions
        {
            Kind = CatalogSourceKind.Postgres,
            Postgres = new PostgresOptions { ConnectionString = "Host=localhost;Database=fusebox" },
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void A_refresh_interval_under_five_seconds_is_rejected(int seconds)
    {
        Assert.True(_validator.Validate(null, new CatalogSourceOptions { RefreshInterval = TimeSpan.FromSeconds(seconds) }).Failed);
    }

    [Fact]
    public void A_sync_interval_under_a_second_is_rejected()
    {
        Assert.True(_validator.Validate(null, new CatalogSourceOptions { SyncInterval = TimeSpan.FromMilliseconds(500) }).Failed);
    }
}

public class CatalogJsonTests
{
    private static readonly CatalogData Sample = CatalogSamples.CatalogOf(
        CatalogSamples.Tenant("acme", CatalogSamples.KeyHashA, budgetUsd: 12.5m),
        CatalogSamples.Tenant("globex", CatalogSamples.KeyHashB));

    [Fact]
    public void A_catalog_survives_a_round_trip_through_json()
    {
        Assert.True(CatalogJson.TryDeserialize(CatalogJson.Serialize(Sample), out var restored));

        Assert.Equal(CatalogJson.Hash(Sample), CatalogJson.Hash(restored!));
        Assert.Equal(Money.FromDollars(12.5m), restored!.Tenants.Single(t => t.Tenant.Id == TenantId.Parse("acme")).Tenant.Budget.Limit);
    }

    [Fact]
    public void Equal_catalogs_have_equal_hashes_whatever_order_their_rows_came_in()
    {
        var reversed = new CatalogData([.. Sample.Tenants.Reverse()], [.. Sample.Prices.Reverse()]);

        Assert.Equal(CatalogJson.Hash(Sample), CatalogJson.Hash(reversed));
    }

    [Fact]
    public void Any_change_changes_the_hash()
    {
        var changed = CatalogSamples.CatalogOf(
            CatalogSamples.Tenant("acme", CatalogSamples.KeyHashA, budgetUsd: 12.6m),
            CatalogSamples.Tenant("globex", CatalogSamples.KeyHashB));

        Assert.NotEqual(CatalogJson.Hash(Sample), CatalogJson.Hash(changed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"Tenants":null,"Prices":[]}""")]
    [InlineData("""{"Tenants":[{"Id":"bad id!","Name":"x","ApiKeyHash":"a","BudgetLimitMicroUsd":1,"BudgetWindowSeconds":3600}],"Prices":[]}""")]
    [InlineData("""{"Tenants":[{"Id":"acme","Name":"x","ApiKeyHash":"a","BudgetLimitMicroUsd":-5,"BudgetWindowSeconds":3600}],"Prices":[]}""")]
    [InlineData("""{"Tenants":[{"Id":"acme","Name":"x","ApiKeyHash":"a","BudgetLimitMicroUsd":5,"BudgetWindowSeconds":10}],"Prices":[]}""")]
    [InlineData("""{"Tenants":[],"Prices":[{"Model":"m","InputMicroUsdPerMillion":-1,"OutputMicroUsdPerMillion":1}]}""")]
    public void Json_that_is_not_a_valid_catalog_is_rejected_instead_of_trusted(string json)
    {
        Assert.False(CatalogJson.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(json), out _));
    }
}
