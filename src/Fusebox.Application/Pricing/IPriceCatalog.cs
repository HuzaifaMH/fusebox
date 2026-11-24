using Fusebox.Domain;

namespace Fusebox.Application.Pricing;

/// <summary>Port: what a tenant pays for each model: the vendor's list price, adjusted by the tenant's own terms.</summary>
public interface IPriceCatalog
{
    /// <returns>The model's price for this tenant, or null if the model is not priced.</returns>
    ValueTask<ModelPrice?> FindAsync(TenantId tenantId, string model, CancellationToken cancellationToken);
}
