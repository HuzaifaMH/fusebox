using System.Globalization;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Enforcement;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Fusebox.Infrastructure.Redis;

/// <summary>
/// Keeps the catalog in Redis as numbered versions. A new version is written in full under its own key and only then
/// made current by one write to the pointer key, so a reader sees the old catalog or the new one, never a mix.
/// Only config lives here: the spend counters are never touched by a catalog refresh.
/// </summary>
internal sealed partial class RedisCatalogCache(
    IConnectionMultiplexer redis,
    IOptions<EnforcementOptions> options,
    ILogger<RedisCatalogCache> logger) : ICatalogCache
{
    // How long a replaced version stays readable, for a reader that fetched the pointer just before it moved.
    private static readonly TimeSpan SupersededRetention = TimeSpan.FromMinutes(10);

    private readonly ILogger<RedisCatalogCache> _logger = logger;

    public async ValueTask<CatalogVersion?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pointer = await redis.GetDatabase().StringGetAsync(PointerKey());
        return pointer.HasValue ? ParsePointer(pointer.ToString()) : null;
    }

    public async ValueTask<CatalogData?> LoadAsync(CatalogVersion version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? blob = await redis.GetDatabase().StringGetAsync(VersionKey(version.Number));
        if (blob is null)
        {
            return null;
        }

        if (!CatalogJson.TryDeserialize(blob, out var data) || data is null || CatalogJson.Hash(data) != version.Hash)
        {
            LogCorruptVersion(version.Number);
            return null;
        }

        return data;
    }

    public async ValueTask<CatalogVersion?> PublishAsync(CatalogData data, string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = redis.GetDatabase();
        var current = await GetCurrentAsync(cancellationToken);

        if (current is { } existing && existing.Hash == hash && await database.KeyExistsAsync(VersionKey(existing.Number)))
        {
            return existing;
        }

        var number = await database.StringIncrementAsync(SequenceKey());
        await database.StringSetAsync(VersionKey(number), CatalogJson.Serialize(data));

        // The switch: one write makes the new version current for every reader at once.
        await database.StringSetAsync(PointerKey(), string.Create(CultureInfo.InvariantCulture, $"{number}:{hash}"));

        if (current is { } replaced)
        {
            await database.KeyExpireAsync(VersionKey(replaced.Number), SupersededRetention);
        }

        LogPublished(number, hash[..12]);
        return new CatalogVersion(number, hash);
    }

    private static CatalogVersion? ParsePointer(string pointer)
    {
        var separator = pointer.IndexOf(':', StringComparison.Ordinal);

        return separator > 0 && long.TryParse(pointer.AsSpan(0, separator), CultureInfo.InvariantCulture, out var number)
            ? new CatalogVersion(number, pointer[(separator + 1)..])
            : null;
    }

    private RedisKey PointerKey() => $"{options.Value.Redis.KeyPrefix}:catalog:current";

    private RedisKey SequenceKey() => $"{options.Value.Redis.KeyPrefix}:catalog:sequence";

    private RedisKey VersionKey(long number) => $"{options.Value.Redis.KeyPrefix}:catalog:version:{number}";

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information,
        Message = "Published catalog version {Version} ({Hash}) to Redis")]
    private partial void LogPublished(long version, string hash);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Error,
        Message = "Catalog version {Version} in Redis is damaged, so it was ignored")]
    private partial void LogCorruptVersion(long version);
}
