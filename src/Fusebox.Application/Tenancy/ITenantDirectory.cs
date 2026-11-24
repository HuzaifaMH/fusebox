using Fusebox.Domain;

namespace Fusebox.Application.Tenancy;

/// <summary>Port: looks up tenants. Implemented by whichever store holds tenant configuration.</summary>
public interface ITenantDirectory
{
    /// <summary>Finds the tenant that owns the API key with this hash, or null if none does.</summary>
    ValueTask<Tenant?> FindByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken);
}
