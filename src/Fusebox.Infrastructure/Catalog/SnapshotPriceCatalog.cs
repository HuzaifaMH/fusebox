using Fusebox.Application.Pricing;
using Fusebox.Domain;

namespace Fusebox.Infrastructure.Catalog;

/// <summary>Prices a call from the current catalog: the tenant's own rate if it has one, otherwise list price times its multiplier.</summary>
internal sealed class SnapshotPriceCatalog(CatalogSnapshotProvider snapshots) : IPriceCatalog
{
    public ValueTask<ModelPrice?> FindAsync(TenantId tenantId, string model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(model);

        var snapshot = snapshots.Current;
        snapshot.PricesByModel.TryGetValue(model, out var listPrice);

        // A tenant that is not in the catalog cannot call, so this only happens for one that was just removed. List price is the safe answer.
        var pricing = snapshot.TenantsById.TryGetValue(tenantId.Value, out var tenant) ? tenant.Pricing : TenantPricing.List;

        return ValueTask.FromResult(pricing.PriceOf(model, listPrice));
    }
}
