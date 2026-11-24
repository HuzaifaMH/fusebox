using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static Fusebox.Infrastructure.Tests.CatalogSamples;

namespace Fusebox.Infrastructure.Tests;

public class CatalogRefresherTests
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Sync = TimeSpan.FromSeconds(15);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-03-04T10:15:00Z"));
    private readonly CatalogSnapshotProvider _snapshots = new();
    private readonly FakeCatalogCache _cache = new();
    private readonly CatalogData _catalogOne = CatalogOf(Tenant("acme", KeyHashA));
    private readonly CatalogData _catalogTwo = CatalogOf(Tenant("acme", KeyHashA), Tenant("globex", KeyHashB));

    private CatalogRefresher Create(FakeCatalogSource source, ICatalogCache? cache = null) => new(
        source,
        cache ?? _cache,
        _snapshots,
        Options.Create(new CatalogSourceOptions { RefreshInterval = Refresh, SyncInterval = Sync }),
        _time,
        NullLogger<CatalogRefresher>.Instance);

    private IReadOnlyCollection<string> ServedKeys => _snapshots.Current.TenantsByKeyHash.Keys;

    [Fact]
    public async Task Loads_the_catalog_from_the_source_before_serving_and_publishes_it()
    {
        var source = new FakeCatalogSource(_catalogOne);

        await Create(source).InitializeAsync(CancellationToken.None);

        Assert.Equal([KeyHashA], ServedKeys);
        Assert.Equal(1, source.Prepares);
        Assert.Equal(1, _cache.Publishes);
    }

    [Fact]
    public async Task Starts_from_the_cached_catalog_when_the_source_is_down()
    {
        _cache.PublishFromElsewhere(_catalogTwo);
        var source = new FakeCatalogSource(_catalogOne) { Failure = new InvalidOperationException("database is down") };

        await Create(source).InitializeAsync(CancellationToken.None);

        Assert.Equal(2, ServedKeys.Count);
    }

    [Fact]
    public async Task Refuses_to_start_when_the_catalog_can_be_read_from_neither_the_source_nor_the_cache()
    {
        var source = new FakeCatalogSource(_catalogOne) { Failure = new InvalidOperationException("database is down") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(source).InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Still_loads_when_preparing_the_source_fails()
    {
        var source = new FakeCatalogSource(_catalogOne) { PrepareFailure = new InvalidOperationException("cannot create tables") };

        await Create(source).InitializeAsync(CancellationToken.None);

        Assert.Equal([KeyHashA], ServedKeys);
    }

    [Fact]
    public async Task Keeps_serving_the_last_catalog_when_a_reload_fails()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);

        source.Failure = new InvalidOperationException("database is down");
        _time.Advance(Refresh);
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal([KeyHashA], ServedKeys);
    }

    [Fact]
    public async Task Retries_at_the_next_tick_after_a_failed_reload_instead_of_waiting_out_the_interval()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);
        source.Failure = new InvalidOperationException("database is down");
        _time.Advance(Refresh);
        await refresher.TickAsync(CancellationToken.None);

        source.Failure = null;
        source.Data = _catalogTwo;
        _time.Advance(Sync);
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal(2, ServedKeys.Count);
    }

    [Fact]
    public async Task Reloads_from_the_source_only_when_the_interval_has_passed()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);
        source.Data = _catalogTwo;

        _time.Advance(Sync);
        await refresher.TickAsync(CancellationToken.None);
        Assert.Equal(1, source.Loads);
        Assert.Single(ServedKeys);

        _time.Advance(Refresh);
        await refresher.TickAsync(CancellationToken.None);
        Assert.Equal(2, source.Loads);
        Assert.Equal(2, ServedKeys.Count);
    }

    [Fact]
    public async Task A_reload_that_changed_nothing_replaces_nothing_and_publishes_nothing()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);
        var before = _snapshots.Current;

        _time.Advance(Refresh);
        await refresher.TickAsync(CancellationToken.None);

        Assert.Same(before, _snapshots.Current);
        Assert.Equal(1, _cache.Publishes);
    }

    [Fact]
    public async Task Reloads_at_the_next_tick_when_the_source_says_it_changed()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);

        source.Data = _catalogTwo;
        source.RaiseChanged();
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal(2, ServedKeys.Count);
    }

    [Fact]
    public async Task Adopts_a_newer_catalog_published_by_another_instance_without_waiting_for_its_own_reload()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);

        _cache.PublishFromElsewhere(_catalogTwo);
        _time.Advance(Sync);
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal(2, ServedKeys.Count);
        Assert.Equal(1, source.Loads);
    }

    [Fact]
    public async Task Does_not_go_back_to_an_older_cached_catalog_after_loading_a_fresher_one_it_could_not_publish()
    {
        _cache.PublishFromElsewhere(_catalogOne);
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);

        // The cache is down while the source returns a newer catalog, so it cannot be published.
        _cache.Down = true;
        source.Data = _catalogTwo;
        _time.Advance(Refresh);
        await refresher.TickAsync(CancellationToken.None);
        Assert.Equal(2, ServedKeys.Count);

        // The cache comes back still holding the old catalog.
        _cache.Down = false;
        _time.Advance(Sync);
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal(2, ServedKeys.Count);
    }

    [Fact]
    public async Task Keeps_serving_when_the_cache_is_down()
    {
        var source = new FakeCatalogSource(_catalogOne);
        _cache.Down = true;
        var refresher = Create(source);

        await refresher.InitializeAsync(CancellationToken.None);
        _time.Advance(Sync);
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal([KeyHashA], ServedKeys);
    }

    [Fact]
    public async Task Rejects_a_catalog_that_cannot_be_indexed_and_keeps_serving_the_last_one()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.InitializeAsync(CancellationToken.None);
        var publishesBefore = _cache.Publishes;

        // Two tenants sharing one API key hash.
        source.Data = CatalogOf(Tenant("acme", KeyHashA), Tenant("globex", KeyHashA));
        _time.Advance(Refresh);
        await refresher.TickAsync(CancellationToken.None);

        Assert.Equal([KeyHashA], ServedKeys);
        Assert.Equal(TenantId.Parse("acme"), _snapshots.Current.TenantsByKeyHash[KeyHashA].Id);
        Assert.Equal(publishesBefore, _cache.Publishes);
    }

    [Fact]
    public async Task Refreshes_on_its_own_as_time_passes_once_started()
    {
        var source = new FakeCatalogSource(_catalogOne);
        var refresher = Create(source);
        await refresher.StartAsync(CancellationToken.None);

        try
        {
            source.Data = _catalogTwo;
            _time.Advance(Refresh + Sync);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (_snapshots.Current.TenantsByKeyHash.Count < 2 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
                _time.Advance(Sync);
            }

            Assert.Equal(2, ServedKeys.Count);
        }
        finally
        {
            await refresher.StopAsync(CancellationToken.None);
        }
    }
}
