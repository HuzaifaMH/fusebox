using Fusebox.Domain;

namespace Fusebox.Application.Admin;

/// <summary>A tenant as the admin sees it. Never includes the key, which is kept only as a hash.</summary>
public sealed record TenantSummary(
    TenantId Id,
    string Name,
    BudgetPolicy Budget,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>What the tenant pays compared with list price, in ten-thousandths: 10,000 is list price, 8,500 is 15% off.</summary>
    public int PriceMultiplierBasisPoints { get; init; } = TenantPricing.ListBasisPoints;
}

/// <summary>A model's price and when it starts to apply. A later row for the same model takes over from it.</summary>
public sealed record PriceSummary(string Model, ModelPrice Price, DateTimeOffset EffectiveFrom);

/// <summary>A tenant to add, with the hash of the key it was given.</summary>
public sealed record NewTenant(Tenant Tenant, string ApiKeyHash);

/// <summary>A tenant that was just created, and its key. The key is shown only this once.</summary>
public sealed record CreatedTenant(TenantSummary Tenant, string ApiKey);

/// <summary>A tenant's new key. Shown only this once.</summary>
public sealed record RotatedKey(TenantId TenantId, string ApiKey);

/// <summary>Port: reads and changes the tenants and prices that the gateway's catalog is loaded from.</summary>
public interface ICatalogAdminStore
{
    ValueTask<IReadOnlyList<TenantSummary>> ListTenantsAsync(CancellationToken cancellationToken);

    ValueTask<TenantSummary?> FindTenantAsync(TenantId id, CancellationToken cancellationToken);

    /// <summary>Adds the tenant. Returns false if a tenant with that id already exists.</summary>
    ValueTask<bool> TryAddTenantAsync(NewTenant tenant, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Returns false if there is no such tenant.</summary>
    ValueTask<bool> SetBudgetAsync(TenantId id, BudgetPolicy budget, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Returns false if there is no such tenant.</summary>
    ValueTask<bool> SetEnabledAsync(TenantId id, bool enabled, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Replaces the tenant's key hash, so the old key stops working. Returns false if there is no such tenant.</summary>
    ValueTask<bool> ReplaceKeyHashAsync(TenantId id, string apiKeyHash, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Sets the multiplier on list prices for this tenant. Returns false if there is no such tenant.</summary>
    ValueTask<bool> SetPriceMultiplierAsync(TenantId id, int basisPoints, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The rates negotiated for this tenant, newest first for each model, including ones scheduled for later.</summary>
    ValueTask<IReadOnlyList<PriceSummary>> ListTenantPricesAsync(TenantId id, CancellationToken cancellationToken);

    /// <summary>Sets this tenant's own rate for a model. Returns false if there is no such tenant.</summary>
    ValueTask<bool> SetTenantPriceAsync(TenantId id, string model, ModelPrice price, DateTimeOffset effectiveFrom, CancellationToken cancellationToken);

    /// <summary>Removes every rate negotiated for this tenant and model, so it pays the list price again. Returns how many were removed.</summary>
    ValueTask<int> RemoveTenantPricesAsync(TenantId id, string model, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<PriceSummary>> ListPricesAsync(CancellationToken cancellationToken);

    /// <summary>Sets a model's price from <paramref name="effectiveFrom"/>. Setting the same moment again replaces it.</summary>
    ValueTask SetPriceAsync(string model, ModelPrice price, DateTimeOffset effectiveFrom, CancellationToken cancellationToken);
}

/// <summary>Port: asks this gateway to load the catalog from its source soon, so a change shows up without waiting for the next scheduled reload.</summary>
public interface ICatalogReload
{
    void RequestReload();
}
