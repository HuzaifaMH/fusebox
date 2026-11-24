using System.Collections.Frozen;
using Fusebox.Domain;

namespace Fusebox.Infrastructure.Catalog;

/// <summary>The catalog indexed for lookup. Immutable once built, so requests read it without locking.</summary>
internal sealed class CatalogSnapshot
{
    private CatalogSnapshot(
        string contentHash,
        DateTimeOffset loadedAt,
        FrozenDictionary<string, Tenant> tenantsByKeyHash,
        FrozenDictionary<string, Tenant> tenantsById,
        FrozenDictionary<string, ModelPrice> pricesByModel)
    {
        ContentHash = contentHash;
        LoadedAt = loadedAt;
        TenantsByKeyHash = tenantsByKeyHash;
        TenantsById = tenantsById;
        PricesByModel = pricesByModel;
    }

    /// <summary>Equal for equal catalogs, so a reload that changed nothing can be told apart from one that did.</summary>
    public string ContentHash { get; }

    public DateTimeOffset LoadedAt { get; }

    public FrozenDictionary<string, Tenant> TenantsByKeyHash { get; }

    /// <summary>The same tenants by id, for looking up what a tenant pays.</summary>
    public FrozenDictionary<string, Tenant> TenantsById { get; }

    public FrozenDictionary<string, ModelPrice> PricesByModel { get; }

    /// <exception cref="ArgumentException">Two tenants share an API key hash, or a model is priced twice.</exception>
    public static CatalogSnapshot Build(CatalogData data, string contentHash, DateTimeOffset loadedAt)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new CatalogSnapshot(
            contentHash,
            loadedAt,
            data.Tenants.ToFrozenDictionary(t => t.ApiKeyHash, t => t.Tenant, StringComparer.Ordinal),
            data.Tenants.ToFrozenDictionary(t => t.Tenant.Id.Value, t => t.Tenant, StringComparer.Ordinal),
            data.Prices.ToFrozenDictionary(p => p.Model, p => p.Price, StringComparer.Ordinal));
    }
}

/// <summary>Holds the catalog the gateway is serving from right now. Replaced as a whole, never edited.</summary>
internal sealed class CatalogSnapshotProvider
{
    private volatile CatalogSnapshot? _current;

    /// <exception cref="InvalidOperationException">No catalog has been loaded yet.</exception>
    public CatalogSnapshot Current =>
        _current ?? throw new InvalidOperationException("The tenant and price catalog has not been loaded yet.");

    /// <summary>The catalog being served, or null before the first load.</summary>
    public CatalogSnapshot? CurrentOrNull => _current;

    public void Replace(CatalogSnapshot snapshot) => _current = snapshot;
}
