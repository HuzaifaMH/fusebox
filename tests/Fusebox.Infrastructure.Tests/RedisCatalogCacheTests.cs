using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Enforcement;
using Fusebox.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using static Fusebox.Infrastructure.Tests.CatalogSamples;

namespace Fusebox.Infrastructure.Tests;

/// <summary>The shared catalog cache, run against a real Redis. Each test uses its own key prefix.</summary>
public class RedisCatalogCacheTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private readonly string _prefix = $"catalog-test-{Guid.NewGuid():N}";
    private readonly CatalogData _one = CatalogOf(Tenant("acme", KeyHashA));
    private readonly CatalogData _two = CatalogOf(Tenant("acme", KeyHashA), Tenant("globex", KeyHashB));

    private RedisCatalogCache CreateCache()
    {
        Skip.If(redis.Connection is null, redis.UnavailableReason);

        return new RedisCatalogCache(
            redis.Connection!,
            Options.Create(new EnforcementOptions { Redis = new RedisOptions { KeyPrefix = _prefix } }),
            NullLogger<RedisCatalogCache>.Instance);
    }

    private static Task<CatalogVersion?> PublishAsync(RedisCatalogCache cache, CatalogData data) =>
        cache.PublishAsync(data, CatalogJson.Hash(data), CancellationToken.None).AsTask();

    [SkippableFact]
    public async Task Has_no_current_version_until_one_is_published()
    {
        var cache = CreateCache();

        Assert.Null(await cache.GetCurrentAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_published_catalog_can_be_read_back_unchanged()
    {
        var cache = CreateCache();

        var published = await PublishAsync(cache, _two);
        var current = await cache.GetCurrentAsync(CancellationToken.None);
        var loaded = await cache.LoadAsync(current!.Value, CancellationToken.None);

        Assert.Equal(published, current);
        Assert.Equal(CatalogJson.Hash(_two), CatalogJson.Hash(loaded!));
    }

    [SkippableFact]
    public async Task Publishing_the_same_catalog_again_does_not_create_a_new_version()
    {
        var cache = CreateCache();

        var first = await PublishAsync(cache, _one);
        var second = await PublishAsync(cache, _one);

        Assert.Equal(first, second);
    }

    [SkippableFact]
    public async Task Publishing_a_changed_catalog_makes_a_newer_version_current_and_lets_the_old_one_expire()
    {
        var cache = CreateCache();
        var first = (await PublishAsync(cache, _one))!.Value;

        var second = (await PublishAsync(cache, _two))!.Value;

        Assert.True(second.Number > first.Number);
        Assert.Equal(second, await cache.GetCurrentAsync(CancellationToken.None));

        var database = redis.Connection!.GetDatabase();
        Assert.NotNull(await database.KeyTimeToLiveAsync($"{_prefix}:catalog:version:{first.Number}"));
        Assert.Null(await database.KeyTimeToLiveAsync($"{_prefix}:catalog:version:{second.Number}"));
    }

    [SkippableFact]
    public async Task Publishes_again_when_the_current_version_has_gone_missing()
    {
        var cache = CreateCache();
        var first = (await PublishAsync(cache, _one))!.Value;
        await redis.Connection!.GetDatabase().KeyDeleteAsync($"{_prefix}:catalog:version:{first.Number}");

        var republished = (await PublishAsync(cache, _one))!.Value;

        Assert.NotNull(await cache.LoadAsync(republished, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Reads_nothing_from_a_version_that_is_gone()
    {
        var cache = CreateCache();

        Assert.Null(await cache.LoadAsync(new CatalogVersion(999, "nothing"), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Does_not_trust_a_damaged_version()
    {
        var cache = CreateCache();
        var published = (await PublishAsync(cache, _one))!.Value;
        await redis.Connection!.GetDatabase().StringSetAsync($"{_prefix}:catalog:version:{published.Number}", "{ not a catalog");

        Assert.Null(await cache.LoadAsync(published, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Does_not_trust_a_version_whose_content_does_not_match_its_hash()
    {
        var cache = CreateCache();
        var published = (await PublishAsync(cache, _one))!.Value;
        await redis.Connection!.GetDatabase().StringSetAsync($"{_prefix}:catalog:version:{published.Number}", CatalogJson.Serialize(_two));

        Assert.Null(await cache.LoadAsync(published, CancellationToken.None));
    }
}
