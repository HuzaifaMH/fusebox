using Fusebox.Domain;

namespace Fusebox.Application.Tenancy;

internal sealed class TenantAuthenticator(IApiKeyHasher hasher, ITenantDirectory directory) : ITenantAuthenticator
{
    public ValueTask<Tenant?> AuthenticateAsync(string? apiKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ValueTask.FromResult<Tenant?>(null);
        }

        return directory.FindByApiKeyHashAsync(hasher.Hash(apiKey), cancellationToken);
    }
}
