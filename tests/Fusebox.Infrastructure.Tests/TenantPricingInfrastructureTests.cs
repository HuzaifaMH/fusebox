using System.Text;
using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Tests;

/// <summary>What a tenant pays, from the catalog's data to the price a call is charged at.</summary>
public class TenantPricingInfrastructureTests
{
    private static readonly ModelPrice List = new(Money.FromDollars(10m), Money.FromDollars(30m));

    private static Tenant TenantWith(string id, TenantPricing pricing) =>
        new(TenantId.Parse(id), id, new BudgetPolicy(Money.FromDollars(10m), TimeSpan.FromHours(1))) { Pricing = pricing };

    private static SnapshotPriceCatalog CatalogOf(params Tenant[] tenants)
    {
        var data = new CatalogData(
            [.. tenants.Select((t, i) => new TenantEntry(new string((char)('a' + i), 64), t))],
            [new PriceEntry("m", List)]);

        var provider = new CatalogSnapshotProvider();
        provider.Replace(CatalogSnapshot.Build(data, "hash", DateTimeOffset.UnixEpoch));
        return new SnapshotPriceCatalog(provider);
    }

    [Fact]
    public async Task A_tenant_on_list_terms_pays_the_list_price()
    {
        var catalog = CatalogOf(TenantWith("acme", TenantPricing.List));

        Assert.Equal(List, await catalog.FindAsync(TenantId.Parse("acme"), "m", CancellationToken.None));
    }

    [Fact]
    public async Task A_multiplier_applies_to_that_tenant_only()
    {
        var catalog = CatalogOf(TenantWith("acme", new TenantPricing(8_500)), TenantWith("globex", TenantPricing.List));

        var discounted = await catalog.FindAsync(TenantId.Parse("acme"), "m", CancellationToken.None);
        var full = await catalog.FindAsync(TenantId.Parse("globex"), "m", CancellationToken.None);

        Assert.Equal(Money.FromDollars(8.5m), discounted!.InputPerMillionTokens);
        Assert.Equal(List, full);
    }

    [Fact]
    public async Task A_negotiated_rate_wins_over_list_price_and_multiplier()
    {
        var rate = new ModelPrice(Money.FromDollars(1m), Money.FromDollars(2m));
        var catalog = CatalogOf(TenantWith("acme", new TenantPricing(8_500, new Dictionary<string, ModelPrice> { ["m"] = rate })));

        Assert.Equal(rate, await catalog.FindAsync(TenantId.Parse("acme"), "m", CancellationToken.None));
    }

    [Fact]
    public async Task A_model_nobody_prices_has_no_price()
    {
        var catalog = CatalogOf(TenantWith("acme", new TenantPricing(8_500)));

        Assert.Null(await catalog.FindAsync(TenantId.Parse("acme"), "unlisted", CancellationToken.None));
    }

    [Fact]
    public async Task A_tenant_that_is_not_in_the_catalog_is_priced_at_list()
    {
        var catalog = CatalogOf(TenantWith("acme", new TenantPricing(5_000)));

        Assert.Equal(List, await catalog.FindAsync(TenantId.Parse("ghost"), "m", CancellationToken.None));
    }

    [Fact]
    public void Pricing_survives_the_trip_through_the_shared_cache()
    {
        var rate = new ModelPrice(Money.FromDollars(1m), Money.FromDollars(2m));
        var data = new CatalogData(
            [new TenantEntry(new string('a', 64), TenantWith("acme", new TenantPricing(8_500, new Dictionary<string, ModelPrice> { ["m"] = rate })))],
            [new PriceEntry("m", List)]);

        Assert.True(CatalogJson.TryDeserialize(CatalogJson.Serialize(data), out var back));

        var tenant = Assert.Single(back!.Tenants).Tenant;
        Assert.Equal(8_500, tenant.Pricing.MultiplierBasisPoints);
        Assert.Equal(rate, tenant.Pricing.ModelPrices["m"]);
    }

    [Fact]
    public void A_change_in_pricing_changes_the_catalogs_hash_so_other_instances_notice()
    {
        CatalogData With(int basisPoints) => new(
            [new TenantEntry(new string('a', 64), TenantWith("acme", new TenantPricing(basisPoints)))],
            [new PriceEntry("m", List)]);

        Assert.NotEqual(CatalogJson.Hash(With(10_000)), CatalogJson.Hash(With(9_000)));
        Assert.Equal(CatalogJson.Hash(With(9_000)), CatalogJson.Hash(With(9_000)));
    }

    [Fact]
    public void A_catalog_written_before_pricing_existed_reads_as_list_price()
    {
        var oldJson = """
            {"Tenants":[{"Id":"acme","Name":"Acme","ApiKeyHash":"__HASH__","BudgetLimitMicroUsd":1000000,"BudgetWindowSeconds":3600}],
             "Prices":[{"Model":"m","InputMicroUsdPerMillion":10000000,"OutputMicroUsdPerMillion":30000000}]}
            """.Replace("__HASH__", new string('a', 64), StringComparison.Ordinal);

        Assert.True(CatalogJson.TryDeserialize(Encoding.UTF8.GetBytes(oldJson), out var data));
        Assert.Equal(TenantPricing.List, Assert.Single(data!.Tenants).Tenant.Pricing);
    }

    [Fact]
    public async Task The_configuration_source_reads_the_multiplier_and_negotiated_rates()
    {
        var options = new CatalogOptions
        {
            Tenants =
            {
                ["acme"] = new TenantOptions
                {
                    Name = "Acme",
                    ApiKeyHash = new string('a', 64),
                    BudgetLimitUsd = 5m,
                    PriceMultiplier = 0.85m,
                    ModelPrices = { ["m"] = new ModelPriceOptions { InputUsdPerMillionTokens = 1m, OutputUsdPerMillionTokens = 2m } },
                },
            },
            Models = { ["m"] = new ModelPriceOptions { InputUsdPerMillionTokens = 10m, OutputUsdPerMillionTokens = 30m } },
        };

        var data = await new ConfigurationCatalogSource(new StaticOptionsMonitor<CatalogOptions>(options)).LoadAsync(CancellationToken.None);

        var pricing = Assert.Single(data.Tenants).Tenant.Pricing;
        Assert.Equal(8_500, pricing.MultiplierBasisPoints);
        Assert.Equal(Money.FromDollars(1m), pricing.ModelPrices["m"].InputPerMillionTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(0.00001)]
    public void The_validator_refuses_a_multiplier_that_cannot_be_right(double multiplier)
    {
        var options = new CatalogOptions
        {
            Tenants = { ["acme"] = new TenantOptions { Name = "Acme", ApiKeyHash = new string('a', 64), BudgetLimitUsd = 5m, PriceMultiplier = (decimal)multiplier } },
        };

        Assert.True(new CatalogOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void The_validator_refuses_a_negative_negotiated_rate()
    {
        var options = new CatalogOptions
        {
            Tenants =
            {
                ["acme"] = new TenantOptions
                {
                    Name = "Acme",
                    ApiKeyHash = new string('a', 64),
                    BudgetLimitUsd = 5m,
                    ModelPrices = { ["m"] = new ModelPriceOptions { InputUsdPerMillionTokens = -1m } },
                },
            },
        };

        Assert.True(new CatalogOptionsValidator().Validate(null, options).Failed);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
