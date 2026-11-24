using Fusebox.Application.Tenancy;
using Fusebox.Domain;

namespace Fusebox.Infrastructure.Catalog;

internal sealed class SnapshotTenantDirectory(CatalogSnapshotProvider snapshots) : ITenantDirectory
{
    public ValueTask<Tenant?> FindByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken)
    {
        snapshots.Current.TenantsByKeyHash.TryGetValue(apiKeyHash, out var tenant);
        return ValueTask.FromResult(tenant);
    }
}
