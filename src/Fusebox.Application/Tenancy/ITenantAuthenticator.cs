using Fusebox.Domain;

namespace Fusebox.Application.Tenancy;

/// <summary>Use case: identify which tenant an API key belongs to.</summary>
public interface ITenantAuthenticator
{
    /// <returns>The tenant, or null if the key is missing or unknown.</returns>
    ValueTask<Tenant?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken);
}
