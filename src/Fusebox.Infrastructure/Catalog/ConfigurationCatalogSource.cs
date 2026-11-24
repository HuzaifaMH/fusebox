using Fusebox.Domain;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Catalog;

/// <summary>Reads the catalog from the "Fusebox:Catalog" configuration section.</summary>
/// <remarks>Assumes the options passed <see cref="CatalogOptionsValidator"/>, which runs at startup and on reload.</remarks>
internal sealed class ConfigurationCatalogSource(IOptionsMonitor<CatalogOptions> options) : ICatalogSource
{
    public ValueTask<CatalogData> LoadAsync(CancellationToken cancellationToken)
    {
        var catalog = options.CurrentValue;

        var tenants = catalog.Tenants.Select(pair => new TenantEntry(
            pair.Value.ApiKeyHash,
            new Tenant(
                TenantId.Parse(pair.Key),
                pair.Value.Name,
                new BudgetPolicy(Money.FromDollars(pair.Value.BudgetLimitUsd), pair.Value.BudgetWindow))
            {
                Pricing = new TenantPricing(
                    PriceMultiplier.ToBasisPoints(pair.Value.PriceMultiplier),
                    pair.Value.ModelPrices.ToDictionary(
                        m => m.Key,
                        m => new ModelPrice(Money.FromDollars(m.Value.InputUsdPerMillionTokens), Money.FromDollars(m.Value.OutputUsdPerMillionTokens)),
                        StringComparer.Ordinal)),
            }));

        var prices = catalog.Models.Select(pair => new PriceEntry(
            pair.Key,
            new ModelPrice(
                Money.FromDollars(pair.Value.InputUsdPerMillionTokens),
                Money.FromDollars(pair.Value.OutputUsdPerMillionTokens))));

        return ValueTask.FromResult(new CatalogData([.. tenants], [.. prices]));
    }

    public IDisposable? OnChange(Action callback) => options.OnChange(_ => callback());
}
