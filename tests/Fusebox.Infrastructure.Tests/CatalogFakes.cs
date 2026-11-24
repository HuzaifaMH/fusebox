using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;

namespace Fusebox.Infrastructure.Tests;

internal static class CatalogSamples
{
    public static readonly string KeyHashA = new('a', 64);
    public static readonly string KeyHashB = new('b', 64);

    public static TenantEntry Tenant(string id, string keyHash, decimal budgetUsd = 10m) => new(
        keyHash,
        new Tenant(TenantId.Parse(id), id.ToUpperInvariant(), new BudgetPolicy(Money.FromDollars(budgetUsd), TimeSpan.FromHours(1))));

    public static PriceEntry Price(string model, decimal inputUsd = 1m, decimal outputUsd = 2m) =>
        new(model, new ModelPrice(Money.FromDollars(inputUsd), Money.FromDollars(outputUsd)));

    public static CatalogData CatalogOf(params TenantEntry[] tenants) => new(tenants, [Price("m")]);
}

/// <summary>A source whose content and failures a test controls.</summary>
internal sealed class FakeCatalogSource(CatalogData data) : ICatalogSource
{
    private Action? _changed;

    public CatalogData Data { get; set; } = data;

    public Exception? Failure { get; set; }

    public int Loads { get; private set; }

    public int Prepares { get; private set; }

    public Exception? PrepareFailure { get; set; }

    public ValueTask PrepareAsync(CancellationToken cancellationToken)
    {
        Prepares++;
        return PrepareFailure is null ? ValueTask.CompletedTask : throw PrepareFailure;
    }

    public ValueTask<CatalogData> LoadAsync(CancellationToken cancellationToken)
    {
        Loads++;
        return Failure is null ? ValueTask.FromResult(Data) : throw Failure;
    }

    public IDisposable? OnChange(Action callback)
    {
        _changed = callback;
        return null;
    }

    /// <summary>Tells the refresher the content changed, as a configuration reload would.</summary>
    public void RaiseChanged() => _changed?.Invoke();
}

/// <summary>An in-memory stand-in for the shared cache that behaves like the Redis one and can be switched off.</summary>
internal sealed class FakeCatalogCache : ICatalogCache
{
    private readonly Dictionary<long, CatalogData> _versions = [];
    private CatalogVersion? _current;
    private long _sequence;

    public bool Down { get; set; }

    public int Publishes { get; private set; }

    public CatalogVersion? Current => _current;

    public ValueTask<CatalogVersion?> GetCurrentAsync(CancellationToken cancellationToken) =>
        Down ? throw new InvalidOperationException("The cache is down.") : ValueTask.FromResult(_current);

    public ValueTask<CatalogData?> LoadAsync(CatalogVersion version, CancellationToken cancellationToken) =>
        Down
            ? throw new InvalidOperationException("The cache is down.")
            : ValueTask.FromResult(_versions.GetValueOrDefault(version.Number));

    public ValueTask<CatalogVersion?> PublishAsync(CatalogData data, string hash, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new InvalidOperationException("The cache is down.");
        }

        if (_current is { } existing && existing.Hash == hash)
        {
            return ValueTask.FromResult<CatalogVersion?>(existing);
        }

        Publishes++;
        _versions[++_sequence] = data;
        _current = new CatalogVersion(_sequence, hash);
        return ValueTask.FromResult(_current);
    }

    /// <summary>Publishes as another gateway instance would.</summary>
    public CatalogVersion PublishFromElsewhere(CatalogData data) =>
        PublishAsync(data, CatalogJson.Hash(data), CancellationToken.None).AsTask().Result!.Value;
}
