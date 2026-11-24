using Fusebox.Application.Budgeting;
using Fusebox.Infrastructure.Enforcement;
using Fusebox.Infrastructure.Redis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Fusebox.Infrastructure.Tests;

/// <summary>
/// Starts a real Redis in Docker, so the Lua scripts are tested against the real thing.
/// If Docker is not running the tests are skipped, not failed.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public IConnectionMultiplexer? Connection { get; private set; }

    public string? UnavailableReason { get; private set; }

#pragma warning disable CA1031 // Any failure to start Docker means "skip", whatever the cause.
    public async Task InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder("redis:7-alpine").Build();
            await _container.StartAsync();
            Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        }
        catch (Exception exception)
        {
            UnavailableReason = $"Docker and Redis are not available: {exception.GetType().Name}";
        }
    }
#pragma warning restore CA1031

    public async Task DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

public class RedisEnforcementStoreTests(RedisFixture redis) : EnforcementStoreContract, IClassFixture<RedisFixture>
{
    protected override IEnforcementStore CreateStore()
    {
        Skip.If(redis.Connection is null, redis.UnavailableReason);

        return new RedisEnforcementStore(redis.Connection!, Options.Create(new EnforcementOptions
        {
            Store = EnforcementStoreKind.Redis,
            Redis = new RedisOptions { KeyPrefix = "fusebox-test" },
        }));
    }
}
