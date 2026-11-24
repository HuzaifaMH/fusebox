using Fusebox.Application.Admin;
using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Catalog.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fusebox.Infrastructure.Tests;

/// <summary>The admin writes, and that the gateway's own catalog source sees them.</summary>
[Collection(SharedPostgres.Name)]
public class PostgresCatalogAdminStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-04T12:00:00Z");
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null)
        {
            return;
        }

        await using (var connection = new NpgsqlConnection(postgres.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("DROP TABLE IF EXISTS tenant_model_prices, model_prices, tenants", connection);
            await command.ExecuteNonQueryAsync();
        }

        await CreateSource().PrepareAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private IOptions<CatalogSourceOptions> Options() => Microsoft.Extensions.Options.Options.Create(new CatalogSourceOptions
    {
        Kind = CatalogSourceKind.Postgres,
        Postgres = new PostgresOptions { ConnectionString = postgres.ConnectionString! },
    });

    private PostgresCatalogSource CreateSource()
    {
        var source = new PostgresCatalogSource(Options(), NullLogger<PostgresCatalogSource>.Instance);
        _disposables.Add(source);
        return source;
    }

    private PostgresCatalogAdminStore CreateStore()
    {
        var store = new PostgresCatalogAdminStore(Options());
        _disposables.Add(store);
        return store;
    }

    private static NewTenant Tenant(string id, string hash, decimal limit = 10m) =>
        new(new Tenant(TenantId.Parse(id), id.ToUpperInvariant(), new BudgetPolicy(Money.FromDollars(limit), TimeSpan.FromHours(1))), hash);

    [SkippableFact]
    public async Task A_tenant_is_saved_listed_and_found()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();

        Assert.True(await store.TryAddTenantAsync(Tenant("acme", HashA, 12.5m), T0, CancellationToken.None));

        var tenant = await store.FindTenantAsync(TenantId.Parse("acme"), CancellationToken.None);
        Assert.NotNull(tenant);
        Assert.Equal(Money.FromDollars(12.5m), tenant.Budget.Limit);
        Assert.Equal(TimeSpan.FromHours(1), tenant.Budget.Window);
        Assert.True(tenant.Enabled);
        Assert.Equal(T0, tenant.CreatedAt);
        Assert.Single(await store.ListTenantsAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_second_tenant_with_the_same_id_is_refused_and_the_first_is_untouched()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        await store.TryAddTenantAsync(Tenant("acme", HashA, 5m), T0, CancellationToken.None);

        Assert.False(await store.TryAddTenantAsync(Tenant("acme", HashB, 99m), T0, CancellationToken.None));

        var tenant = await store.FindTenantAsync(TenantId.Parse("acme"), CancellationToken.None);
        Assert.Equal(Money.FromDollars(5m), tenant!.Budget.Limit);
    }

    [SkippableFact]
    public async Task Changes_to_an_unknown_tenant_report_not_found()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var ghost = TenantId.Parse("ghost");

        Assert.False(await store.SetBudgetAsync(ghost, new BudgetPolicy(Money.FromDollars(1m), TimeSpan.FromHours(1)), T0, CancellationToken.None));
        Assert.False(await store.SetEnabledAsync(ghost, false, T0, CancellationToken.None));
        Assert.False(await store.ReplaceKeyHashAsync(ghost, HashA, T0, CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_gateways_catalog_source_sees_what_the_admin_changed()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var source = CreateSource();
        await store.TryAddTenantAsync(Tenant("acme", HashA), T0, CancellationToken.None);

        var loaded = await source.LoadAsync(CancellationToken.None);
        Assert.Equal(HashA, Assert.Single(loaded.Tenants).ApiKeyHash);

        await store.SetBudgetAsync(TenantId.Parse("acme"), new BudgetPolicy(Money.FromDollars(40m), TimeSpan.FromDays(1)), T0, CancellationToken.None);
        await store.ReplaceKeyHashAsync(TenantId.Parse("acme"), HashB, T0, CancellationToken.None);

        var changed = Assert.Single((await source.LoadAsync(CancellationToken.None)).Tenants);
        Assert.Equal(HashB, changed.ApiKeyHash);
        Assert.Equal(Money.FromDollars(40m), changed.Tenant.Budget.Limit);
        Assert.Equal(TimeSpan.FromDays(1), changed.Tenant.Budget.Window);

        await store.SetEnabledAsync(TenantId.Parse("acme"), false, T0, CancellationToken.None);
        Assert.Empty((await source.LoadAsync(CancellationToken.None)).Tenants);
    }

    [SkippableFact]
    public async Task A_scheduled_price_does_not_apply_until_its_time_and_the_same_moment_is_replaced()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var source = CreateSource();

        await store.SetPriceAsync("gpt-x", new ModelPrice(Money.FromDollars(1m), Money.FromDollars(2m)), DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);
        await store.SetPriceAsync("gpt-x", new ModelPrice(Money.FromDollars(9m), Money.FromDollars(9m)), DateTimeOffset.UtcNow.AddDays(30), CancellationToken.None);

        var inEffect = Assert.Single((await source.LoadAsync(CancellationToken.None)).Prices);
        Assert.Equal(Money.FromDollars(1m), inEffect.Price.InputPerMillionTokens);

        var at = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
        await store.SetPriceAsync("gpt-y", new ModelPrice(Money.FromDollars(1m), Money.FromDollars(1m)), at, CancellationToken.None);
        await store.SetPriceAsync("gpt-y", new ModelPrice(Money.FromDollars(3m), Money.FromDollars(4m)), at, CancellationToken.None);

        var all = await store.ListPricesAsync(CancellationToken.None);
        var replaced = Assert.Single(all, p => p.Model == "gpt-y");
        Assert.Equal(Money.FromDollars(3m), replaced.Price.InputPerMillionTokens);
        Assert.Equal(3, all.Count);
        Assert.True(all.Where(p => p.Model == "gpt-x").First().EffectiveFrom > DateTimeOffset.UtcNow);
    }

    [SkippableFact]
    public async Task A_multiplier_and_a_negotiated_rate_reach_the_gateways_catalog()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var source = CreateSource();
        var acme = TenantId.Parse("acme");
        await store.TryAddTenantAsync(Tenant("acme", HashA), T0, CancellationToken.None);
        await store.SetPriceAsync("m", new ModelPrice(Money.FromDollars(10m), Money.FromDollars(30m)), DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        Assert.True(await store.SetPriceMultiplierAsync(acme, 8_500, T0, CancellationToken.None));
        Assert.True(await store.SetTenantPriceAsync(acme, "m", new ModelPrice(Money.FromDollars(1m), Money.FromDollars(2m)), DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None));

        var pricing = Assert.Single((await source.LoadAsync(CancellationToken.None)).Tenants).Tenant.Pricing;
        Assert.Equal(8_500, pricing.MultiplierBasisPoints);
        Assert.Equal(Money.FromDollars(1m), pricing.ModelPrices["m"].InputPerMillionTokens);
        Assert.Equal(8_500, (await store.FindTenantAsync(acme, CancellationToken.None))!.PriceMultiplierBasisPoints);
    }

    [SkippableFact]
    public async Task A_scheduled_negotiated_rate_does_not_apply_until_its_time_and_removing_it_restores_list_price()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var source = CreateSource();
        var acme = TenantId.Parse("acme");
        await store.TryAddTenantAsync(Tenant("acme", HashA), T0, CancellationToken.None);

        await store.SetTenantPriceAsync(acme, "m", new ModelPrice(Money.FromDollars(9m), Money.FromDollars(9m)), DateTimeOffset.UtcNow.AddDays(30), CancellationToken.None);
        Assert.Empty(Assert.Single((await source.LoadAsync(CancellationToken.None)).Tenants).Tenant.Pricing.ModelPrices);
        Assert.Single(await store.ListTenantPricesAsync(acme, CancellationToken.None));

        await store.SetTenantPriceAsync(acme, "m", new ModelPrice(Money.FromDollars(1m), Money.FromDollars(1m)), DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);
        Assert.Single(Assert.Single((await source.LoadAsync(CancellationToken.None)).Tenants).Tenant.Pricing.ModelPrices);

        Assert.Equal(2, await store.RemoveTenantPricesAsync(acme, "m", CancellationToken.None));
        Assert.Empty(Assert.Single((await source.LoadAsync(CancellationToken.None)).Tenants).Tenant.Pricing.ModelPrices);
        Assert.Equal(0, await store.RemoveTenantPricesAsync(acme, "m", CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_negotiated_rate_for_an_unknown_tenant_is_refused_without_error()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();

        Assert.False(await store.SetTenantPriceAsync(TenantId.Parse("ghost"), "m", new ModelPrice(Money.FromDollars(1m), Money.FromDollars(1m)), T0, CancellationToken.None));
        Assert.False(await store.SetPriceMultiplierAsync(TenantId.Parse("ghost"), 9_000, T0, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Removing_a_tenant_removes_its_negotiated_rates()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var acme = TenantId.Parse("acme");
        await store.TryAddTenantAsync(Tenant("acme", HashA), T0, CancellationToken.None);
        await store.SetTenantPriceAsync(acme, "m", new ModelPrice(Money.FromDollars(1m), Money.FromDollars(1m)), T0, CancellationToken.None);

        await using (var connection = new NpgsqlConnection(postgres.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("DELETE FROM tenants WHERE id = 'acme'", connection);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Empty(await store.ListTenantPricesAsync(acme, CancellationToken.None));
    }
}
