using System.Net;
using System.Net.Sockets;
using Fusebox.Application.Budgeting;
using Fusebox.Domain;
using Fusebox.Infrastructure.Enforcement;
using Fusebox.Infrastructure.Redis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace Fusebox.Infrastructure.Tests;

/// <summary>
/// The chaos test: stop a real Redis while the gateway is using it, check that calls are still served on the
/// local limit and that the outage is reported, then start Redis again and check that the gateway returns to it.
/// Skipped when Docker is not running.
/// </summary>
public sealed class RedisOutageTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly OutageRecorder _outages = new();
    private readonly TenantId _tenant = TenantId.Parse("acme");
    private RedisContainer? _container;
    private ConnectionMultiplexer? _connection;
    private string? _unavailableReason;

#pragma warning disable CA1031 // Any failure to start Docker means "skip", whatever the cause.
    public async Task InitializeAsync()
    {
        try
        {
            var port = FreePort();

            // A fixed host port, so the connection survives the container being stopped and started again.
            _container = new RedisBuilder("redis:7-alpine").WithPortBinding(port, 6379).Build();
            await _container.StartAsync();

            var configuration = ConfigurationOptions.Parse($"127.0.0.1:{port}");
            configuration.AbortOnConnectFail = false;
            configuration.ConnectTimeout = 2_000;
            configuration.AsyncTimeout = 200;
            _connection = await ConnectionMultiplexer.ConnectAsync(configuration);
        }
        catch (Exception exception)
        {
            _unavailableReason = $"Docker and Redis are not available: {exception.GetType().Name}";
        }
    }
#pragma warning restore CA1031

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private DegradedEnforcementStore CreateStore()
    {
        var time = TimeProvider.System;
        var enforcement = Options.Create(new EnforcementOptions { Redis = new RedisOptions { KeyPrefix = "fusebox-outage-test" } });

        return new DegradedEnforcementStore(
            new RedisEnforcementStore(_connection!, enforcement),
            new InMemoryEnforcementStore(time, enforcement, NullLogger<InMemoryEnforcementStore>.Instance),
            Options.Create(new DegradedModeOptions
            {
                StoreTimeout = TimeSpan.FromMilliseconds(300),
                FailureThreshold = 2,
                ProbeInterval = TimeSpan.FromMilliseconds(500),
                MaxDuration = TimeSpan.FromMinutes(5),
            }),
            time,
            [_outages],
            NullLogger<DegradedEnforcementStore>.Instance);
    }

    private BudgetCharge Charge(long microDollars) => new(
        _tenant,
        Guid.NewGuid(),
        Money.FromMicroDollars(microDollars),
        new BudgetPolicy(Money.FromDollars(1m), TimeSpan.FromHours(1)),
        DateTimeOffset.UtcNow);

    [SkippableFact]
    public async Task Serves_on_a_local_limit_while_redis_is_down_and_returns_to_it_when_it_is_back()
    {
        Skip.If(_container is null, _unavailableReason);
        var store = CreateStore();

        // Healthy: the shared ledger counts the call.
        var healthy = await store.TryReserveAsync(Charge(100_000), CancellationToken.None);
        Assert.True(healthy.Granted);
        Assert.Empty(_outages.Started);

        // Redis goes away. Calls are still served, and the outage is declared once.
        await _container!.StopAsync();
        var duringOutage = new List<ReserveResult>();
        for (var i = 0; i < 5; i++)
        {
            duringOutage.Add(await store.TryReserveAsync(Charge(10_000), CancellationToken.None));
        }

        Assert.All(duringOutage, result => Assert.True(result.Granted));
        Assert.Single(_outages.Started);
        Assert.Empty(_outages.Ended);

        // Redis comes back. Within a few seconds the gateway notices and returns to the shared ledger.
        await _container.StartAsync();
        ReserveResult recovered = default;
        var deadline = DateTimeOffset.UtcNow + Patience;

        while (_outages.Ended.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            recovered = await store.TryReserveAsync(Charge(20_000), CancellationToken.None);
        }

        var summary = Assert.Single(_outages.Ended);
        Assert.True(summary.UnverifiedCalls >= 5);
        Assert.True(summary.UnverifiedSpend[_tenant].MicroDollars >= 50_000);

        // The answer came from the shared ledger again: it still knows the $0.10 spent before the outage, which the
        // local limit never could. It may hold a little more if a test call timed out but reached Redis a moment later.
        Assert.True(recovered.Granted);
        Assert.True(recovered.Spent.MicroDollars >= 120_000);
    }

    private sealed class OutageRecorder : IEnforcementOutageListener
    {
        public List<string> Started { get; } = [];

        public List<OutageSummary> Ended { get; } = [];

        public void OutageStarted(DateTimeOffset at, string reason) => Started.Add(reason);

        public void OutageLimitReached(DateTimeOffset outageStartedAt, DateTimeOffset at)
        {
        }

        public void OutageEnded(OutageSummary summary) => Ended.Add(summary);
    }
}
