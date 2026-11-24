using Fusebox.Application.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Catalog;

/// <summary>
/// Keeps the catalog the gateway serves from up to date, without ever making a request wait for it.
/// </summary>
/// <remarks>
/// The gateway serves from an in-memory snapshot that is replaced whole. The snapshot is filled before the gateway
/// accepts traffic, then kept fresh two ways: it is reloaded from the source every <see cref="CatalogSourceOptions.RefreshInterval"/>
/// (the source of truth, and the safety net), and each reload is published to the shared cache, which the other instances
/// check every <see cref="CatalogSourceOptions.SyncInterval"/>, so a change reaches all of them within seconds.
/// If the source cannot be read, the last catalog keeps serving. Nothing here touches spend counters.
/// </remarks>
internal sealed partial class CatalogRefresher(
    ICatalogSource source,
    ICatalogCache cache,
    CatalogSnapshotProvider snapshots,
    IOptions<CatalogSourceOptions> options,
    TimeProvider time,
    ILogger<CatalogRefresher> logger) : BackgroundService, ICatalogReload
{
    private readonly ILogger<CatalogRefresher> _logger = logger;
    private readonly CatalogSourceOptions _options = options.Value;

    private IDisposable? _changeSubscription;
    private DateTimeOffset _nextRefresh;
    private DateTimeOffset? _lastRefresh;
    private long _cacheVersion;
    private volatile bool _refreshRequested;

    /// <summary>Loads the catalog first, so the gateway does not serve a single request without one.</summary>
    /// <exception cref="InvalidOperationException">The catalog could be read from neither the source nor the cache.</exception>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    /// <summary>The part of starting that must finish before the gateway serves: get the first catalog.</summary>
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await PrepareSourceAsync(cancellationToken);

        if (!await TryRefreshFromSourceAsync(cancellationToken) && !await TrySyncFromCacheAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The tenant and price catalog could not be loaded from its source or from the cache, so the gateway cannot start. "
                + "Without it no caller can be identified or priced.");
        }

        _changeSubscription = source.OnChange(() => _refreshRequested = true);
    }

    /// <summary>Reloads from the source at the next tick, which is within <see cref="CatalogSourceOptions.SyncInterval"/>.</summary>
    public void RequestReload() => _refreshRequested = true;

    public override void Dispose()
    {
        _changeSubscription?.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SyncInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The gateway is stopping.
        }
    }

    /// <summary>One step of the loop: reload from the source if it is due, otherwise check the cache for a newer catalog.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_refreshRequested || time.GetUtcNow() >= _nextRefresh)
        {
            _refreshRequested = false;

            if (await TryRefreshFromSourceAsync(cancellationToken))
            {
                return;
            }
        }

        await TrySyncFromCacheAsync(cancellationToken);
    }

#pragma warning disable CA1031 // Whatever goes wrong reading the source, the last good catalog must keep serving.
    private async Task PrepareSourceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await source.PrepareAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPrepareFailed(exception);
        }
    }

    private async Task<bool> TryRefreshFromSourceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        try
        {
            var data = await source.LoadAsync(cancellationToken);
            var hash = CatalogJson.Hash(data);

            // Build first: a catalog that cannot be indexed (say, two tenants with one key) must not be published.
            var snapshot = CatalogSnapshot.Build(data, hash, now);

            // If it could not be published, this copy is the freshest known, so ignore the cache's older ones until a publish succeeds.
            var published = await PublishAsync(data, hash, cancellationToken);
            _cacheVersion = published?.Number ?? long.MaxValue;

            Apply(snapshot, "source");
            _lastRefresh = now;
            _nextRefresh = now + _options.RefreshInterval;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Try again at the next tick rather than waiting out the whole interval.
            _nextRefresh = now;
            LogRefreshFailed(exception, now - _lastRefresh);
            return false;
        }
    }

    private async Task<bool> TrySyncFromCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = await cache.GetCurrentAsync(cancellationToken);

            // Only a version published after the last one seen can be newer. This stops a stale cache from undoing a fresher load.
            if (current is not { } version || version.Number <= _cacheVersion)
            {
                return false;
            }

            if (snapshots.CurrentOrNull?.ContentHash == version.Hash)
            {
                _cacheVersion = version.Number;
                return false;
            }

            var data = await cache.LoadAsync(version, cancellationToken);
            if (data is null)
            {
                return false;
            }

            Apply(CatalogSnapshot.Build(data, version.Hash, time.GetUtcNow()), "cache");
            _cacheVersion = version.Number;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCacheFailed(exception);
            return false;
        }
    }

    // Publishing is a courtesy to the other instances. If the cache is down, this instance still has the catalog.
    private async Task<CatalogVersion?> PublishAsync(CatalogData data, string hash, CancellationToken cancellationToken)
    {
        try
        {
            return await cache.PublishAsync(data, hash, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogPublishFailed(exception);
            return null;
        }
    }
#pragma warning restore CA1031

    private void Apply(CatalogSnapshot snapshot, string origin)
    {
        if (snapshots.CurrentOrNull?.ContentHash == snapshot.ContentHash)
        {
            return;
        }

        snapshots.Replace(snapshot);
        LogLoaded(origin, snapshot.TenantsByKeyHash.Count, snapshot.PricesByModel.Count, snapshot.ContentHash[..12]);

        if (snapshot.TenantsByKeyHash.Count == 0)
        {
            LogNoTenants(origin);
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "Loaded the catalog from the {Origin}: {Tenants} tenants, {Prices} prices ({Hash})")]
    private partial void LogLoaded(string origin, int tenants, int prices, string hash);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning,
        Message = "The catalog from the {Origin} has no tenants, so every call will be rejected as unauthenticated")]
    private partial void LogNoTenants(string origin);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Error,
        Message = "Could not reload the catalog from its source. Still serving the last one, last confirmed {Age} ago (blank if never)")]
    private partial void LogRefreshFailed(Exception exception, TimeSpan? age);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning,
        Message = "Could not read the shared catalog cache")]
    private partial void LogCacheFailed(Exception exception);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning,
        Message = "Could not publish the catalog to the shared cache; other instances will pick the change up from the source")]
    private partial void LogPublishFailed(Exception exception);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Error,
        Message = "Could not prepare the catalog source; trying to load from it anyway")]
    private partial void LogPrepareFailed(Exception exception);
}
