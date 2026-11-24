using Fusebox.Domain;

namespace Fusebox.Infrastructure.Catalog;

/// <summary>A tenant and the hash of the API key that identifies it.</summary>
internal sealed record TenantEntry(string ApiKeyHash, Tenant Tenant);

/// <summary>What one model costs.</summary>
internal sealed record PriceEntry(string Model, ModelPrice Price);

/// <summary>Everything the gateway needs to know about who may call and what calls cost, at one moment.</summary>
internal sealed record CatalogData(IReadOnlyList<TenantEntry> Tenants, IReadOnlyList<PriceEntry> Prices);

/// <summary>Where the catalog comes from: a database in production, configuration files in development.</summary>
internal interface ICatalogSource
{
    /// <summary>Gets the source ready to be read, for example by creating its tables. Runs once, before the first load.</summary>
    ValueTask PrepareAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Reads the whole catalog. Throws if the source cannot be read.</summary>
    ValueTask<CatalogData> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Calls <paramref name="callback"/> when the source knows its content changed, so it can be reloaded sooner than
    /// the regular interval. Sources that cannot tell return null.
    /// </summary>
    IDisposable? OnChange(Action callback) => null;
}
