namespace Fusebox.Infrastructure.Catalog;

/// <summary>Names one published copy of the catalog. A higher version was published later.</summary>
internal readonly record struct CatalogVersion(long Number, string Hash);

/// <summary>
/// A copy of the catalog shared by every gateway instance, so a catalog change made by one reaches the others
/// quickly, and a gateway that starts while the database is down can still start.
/// </summary>
internal interface ICatalogCache
{
    /// <summary>The newest published version, or null if there is none. A single cheap read.</summary>
    ValueTask<CatalogVersion?> GetCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Reads the catalog of a version. Returns null if it is gone or does not hold up.</summary>
    ValueTask<CatalogData?> LoadAsync(CatalogVersion version, CancellationToken cancellationToken);

    /// <summary>
    /// Makes this catalog the current version unless it already is, and returns the current version.
    /// Returns null if there is no cache to publish to.
    /// </summary>
    ValueTask<CatalogVersion?> PublishAsync(CatalogData data, string hash, CancellationToken cancellationToken);
}

/// <summary>Used when Redis is not the enforcement store, so there is nothing to share the catalog through.</summary>
internal sealed class NoCatalogCache : ICatalogCache
{
    public ValueTask<CatalogVersion?> GetCurrentAsync(CancellationToken cancellationToken) => ValueTask.FromResult<CatalogVersion?>(null);

    public ValueTask<CatalogData?> LoadAsync(CatalogVersion version, CancellationToken cancellationToken) => ValueTask.FromResult<CatalogData?>(null);

    public ValueTask<CatalogVersion?> PublishAsync(CatalogData data, string hash, CancellationToken cancellationToken) =>
        ValueTask.FromResult<CatalogVersion?>(null);
}
