using Fusebox.Domain;

namespace Fusebox.Gateway.Http;

/// <summary>Carries the authenticated tenant from the authentication middleware to later middleware.</summary>
internal sealed record TenantFeature(Tenant Tenant);

internal static class TenantFeatureExtensions
{
    public static void SetTenant(this HttpContext context, Tenant tenant) =>
        context.Features.Set(new TenantFeature(tenant));

    /// <exception cref="InvalidOperationException">No tenant was authenticated, which means the pipeline is mis-ordered.</exception>
    public static Tenant GetTenant(this HttpContext context) =>
        context.Features.Get<TenantFeature>()?.Tenant
        ?? throw new InvalidOperationException("No authenticated tenant. Authentication must run before this middleware.");
}
