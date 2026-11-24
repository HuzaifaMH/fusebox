using Fusebox.Application.Budgeting;
using Fusebox.Domain;
using Fusebox.Infrastructure.Enforcement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Infrastructure.Tests;

/// <summary>
/// The shared store is a real in-memory ledger that a test can switch off or make hang, standing in for a Redis outage.
/// With a $1.00 budget, one gateway instance may spend $0.25 on its own while degraded.
/// </summary>
public class DegradedEnforcementStoreTests
{
    private const long OneDollar = 1_000_000;

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-04T10:15:00Z");

    private readonly FakeTimeProvider _time = new(Now);
    private readonly SwitchableStore _shared;
    private readonly RecordingListener _listener = new();
    private readonly DegradedModeOptions _options = new();
    private readonly TenantId _tenant = TenantId.Parse("acme");

    public DegradedEnforcementStoreTests()
    {
        _shared = new SwitchableStore(NewLedger());
    }

    private InMemoryEnforcementStore NewLedger() => new(
        _time,
        Options.Create(new EnforcementOptions()),
        NullLogger<InMemoryEnforcementStore>.Instance);

    private DegradedEnforcementStore CreateStore(params IEnforcementOutageListener[] extraListeners) => new(
        _shared,
        NewLedger(),
        Options.Create(_options),
        _time,
        [_listener, .. extraListeners],
        NullLogger<DegradedEnforcementStore>.Instance);

    private BudgetCharge Charge(long microDollars, FailureMode mode = FailureMode.FailOpenDegraded) => new(
        _tenant,
        Guid.NewGuid(),
        Money.FromMicroDollars(microDollars),
        new BudgetPolicy(Money.FromMicroDollars(OneDollar), TimeSpan.FromHours(1)),
        Now,
        mode);

    private static Reservation ReservationFor(BudgetCharge charge, ReserveResult result) =>
        new(charge.ReservationId, charge.TenantId, result.Window.Start, charge.Amount, "model");

    /// <summary>Makes enough calls against a dead store to open the circuit and start degraded mode.</summary>
    private async Task StartOutageAsync(DegradedEnforcementStore store)
    {
        _shared.Down = true;

        for (var i = 0; i < _options.FailureThreshold; i++)
        {
            await store.TryReserveAsync(Charge(10_000), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Goes_straight_to_the_shared_store_while_it_is_healthy()
    {
        var store = CreateStore();

        var result = await store.TryReserveAsync(Charge(200_000), CancellationToken.None);

        Assert.True(result.Granted);
        Assert.Equal(200_000, result.Spent.MicroDollars);
        Assert.Equal(1, _shared.ReserveCalls);
        Assert.Empty(_listener.Events);
    }

    [Fact]
    public async Task Serves_a_call_on_the_local_limit_when_the_store_fails_once_without_declaring_an_outage()
    {
        var store = CreateStore();
        _shared.Down = true;

        var result = await store.TryReserveAsync(Charge(100_000), CancellationToken.None);

        Assert.True(result.Granted);
        Assert.Empty(_listener.Events);
    }

    [Fact]
    public async Task Declares_one_outage_after_repeated_failures_and_stops_asking_the_store()
    {
        var store = CreateStore();

        await StartOutageAsync(store);
        var callsWhenOpened = _shared.ReserveCalls;
        await store.TryReserveAsync(Charge(10_000), CancellationToken.None);
        await store.TryReserveAsync(Charge(10_000), CancellationToken.None);

        Assert.Equal(callsWhenOpened, _shared.ReserveCalls);
        var started = Assert.Single(_listener.Started);
        Assert.Equal(Now, started.At);
    }

    [Fact]
    public async Task Holds_each_tenant_to_its_share_of_the_budget_while_degraded()
    {
        var store = CreateStore();
        await StartOutageAsync(store);

        // 3 calls of $0.01 are already spent from the $0.25 local share. $0.20 more fits, $0.03 more than that does not.
        var fits = await store.TryReserveAsync(Charge(200_000), CancellationToken.None);
        var tooMuch = await store.TryReserveAsync(Charge(30_000), CancellationToken.None);

        Assert.True(fits.Granted);
        Assert.False(tooMuch.Granted);
    }

    [Fact]
    public async Task Rejects_a_fail_closed_route_at_once_even_on_the_first_failure()
    {
        var store = CreateStore();
        _shared.Down = true;

        await Assert.ThrowsAsync<EnforcementUnavailableException>(async () =>
            await store.TryReserveAsync(Charge(10_000, FailureMode.FailClosed), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_a_fail_closed_route_during_an_outage_while_other_routes_still_serve()
    {
        var store = CreateStore();
        await StartOutageAsync(store);

        var served = await store.TryReserveAsync(Charge(10_000), CancellationToken.None);

        Assert.True(served.Granted);
        await Assert.ThrowsAsync<EnforcementUnavailableException>(async () =>
            await store.TryReserveAsync(Charge(10_000, FailureMode.FailClosed), CancellationToken.None));
    }

    [Fact]
    public async Task Stops_serving_unverified_calls_once_degraded_mode_has_run_its_course_and_says_so_once()
    {
        var store = CreateStore();
        await StartOutageAsync(store);

        _time.Advance(_options.MaxDuration + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<EnforcementUnavailableException>(async () =>
            await store.TryReserveAsync(Charge(10_000), CancellationToken.None));
        await Assert.ThrowsAsync<EnforcementUnavailableException>(async () =>
            await store.TryReserveAsync(Charge(10_000), CancellationToken.None));

        var reached = Assert.Single(_listener.LimitReached);
        Assert.Equal(Now, reached.StartedAt);
    }

    [Fact]
    public async Task Tests_the_store_after_the_probe_interval_and_returns_to_it_when_it_answers()
    {
        var store = CreateStore();
        await StartOutageAsync(store);
        _shared.Down = false;
        _time.Advance(_options.ProbeInterval);

        var result = await store.TryReserveAsync(Charge(100_000), CancellationToken.None);

        // The shared ledger answered, so this call was counted there and the outage is over.
        Assert.True(result.Granted);
        Assert.Equal(100_000, result.Spent.MicroDollars);
        Assert.Single(_listener.Ended);
    }

    [Fact]
    public async Task Reports_what_was_spent_without_verification_when_the_outage_ends()
    {
        var store = CreateStore();
        await StartOutageAsync(store);
        _shared.Down = false;
        _time.Advance(_options.ProbeInterval);

        await store.TryReserveAsync(Charge(100_000), CancellationToken.None);

        var summary = Assert.Single(_listener.Ended);
        Assert.Equal(Now, summary.StartedAt);
        Assert.Equal(Now + _options.ProbeInterval, summary.EndedAt);
        Assert.Equal(3, summary.UnverifiedCalls);
        Assert.Equal(30_000, summary.UnverifiedSpend[_tenant].MicroDollars);
    }

    [Fact]
    public async Task Waits_a_full_interval_again_when_the_test_call_fails()
    {
        var store = CreateStore();
        await StartOutageAsync(store);
        var callsWhenOpened = _shared.ReserveCalls;
        _time.Advance(_options.ProbeInterval);

        await store.TryReserveAsync(Charge(10_000), CancellationToken.None);
        await store.TryReserveAsync(Charge(10_000), CancellationToken.None);

        Assert.Equal(callsWhenOpened + 1, _shared.ReserveCalls);
        Assert.Empty(_listener.Ended);
    }

    [Fact]
    public async Task Counts_a_store_that_does_not_answer_in_time_as_a_failure()
    {
        var store = CreateStore();
        _shared.Hang = true;

        var pending = store.TryReserveAsync(Charge(100_000), CancellationToken.None).AsTask();
        _time.Advance(_options.StoreTimeout + TimeSpan.FromMilliseconds(10));
        var result = await pending;

        Assert.True(result.Granted);
        Assert.Equal(1, _shared.ReserveCalls);
    }

    [Fact]
    public async Task A_caller_who_gives_up_is_not_counted_against_the_store()
    {
        var store = CreateStore();
        _shared.Hang = true;

        for (var i = 0; i < _options.FailureThreshold + 1; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = store.TryReserveAsync(Charge(10_000), cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        _shared.Hang = false;
        var result = await store.TryReserveAsync(Charge(10_000), CancellationToken.None);

        Assert.Equal(_options.FailureThreshold + 2, _shared.ReserveCalls);
        Assert.Equal(10_000, result.Spent.MicroDollars);
        Assert.Empty(_listener.Events);
    }

    [Fact]
    public async Task Settles_a_call_held_locally_without_asking_the_store_and_reports_the_real_spend()
    {
        var store = CreateStore();
        await StartOutageAsync(store);
        var charge = Charge(200_000);
        var held = await store.TryReserveAsync(charge, CancellationToken.None);

        var outcome = await store.SettleAsync(
            new SettleRequest(ReservationFor(charge, held), Money.FromMicroDollars(50_000)),
            CancellationToken.None);

        // 3 calls of $0.01 plus a call that was held at $0.20 but cost $0.05.
        Assert.Equal(SettleOutcome.Applied, outcome);
        Assert.Equal(0, _shared.SettleCalls);

        _shared.Down = false;
        _time.Advance(_options.ProbeInterval);
        await store.TryReserveAsync(Charge(1_000), CancellationToken.None);
        Assert.Equal(80_000, Assert.Single(_listener.Ended).UnverifiedSpend[_tenant].MicroDollars);
    }

    [Fact]
    public async Task Settles_a_call_held_in_the_shared_store_there()
    {
        var store = CreateStore();
        var charge = Charge(200_000);
        var held = await store.TryReserveAsync(charge, CancellationToken.None);

        var outcome = await store.SettleAsync(
            new SettleRequest(ReservationFor(charge, held), Money.FromMicroDollars(50_000)),
            CancellationToken.None);

        Assert.Equal(SettleOutcome.Applied, outcome);
        Assert.Equal(1, _shared.SettleCalls);
        Assert.Equal(50_000, (await store.TryReserveAsync(Charge(0), CancellationToken.None)).Spent.MicroDollars);
    }

    [Fact]
    public async Task Fails_to_settle_when_the_store_is_down_so_the_hold_stays_counted()
    {
        var store = CreateStore();
        var charge = Charge(200_000);
        var held = await store.TryReserveAsync(charge, CancellationToken.None);
        _shared.Down = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.SettleAsync(
                new SettleRequest(ReservationFor(charge, held), Money.FromMicroDollars(50_000)),
                CancellationToken.None));
    }

    [Fact]
    public async Task A_listener_that_fails_cannot_break_a_request()
    {
        var store = CreateStore(new ThrowingListener());

        await StartOutageAsync(store);
        var result = await store.TryReserveAsync(Charge(10_000), CancellationToken.None);

        Assert.True(result.Granted);
        Assert.Single(_listener.Started);
    }

    private sealed class SwitchableStore(IEnforcementStore inner) : IEnforcementStore
    {
        public bool Down { get; set; }

        public bool Hang { get; set; }

        public int ReserveCalls { get; private set; }

        public int SettleCalls { get; private set; }

        public ValueTask<ReserveResult> TryReserveAsync(BudgetCharge charge, CancellationToken cancellationToken)
        {
            ReserveCalls++;

            if (Down)
            {
                throw new InvalidOperationException("The store is down.");
            }

            return Hang
                ? new ValueTask<ReserveResult>(new TaskCompletionSource<ReserveResult>().Task)
                : inner.TryReserveAsync(charge, cancellationToken);
        }

        public ValueTask<SettleOutcome> SettleAsync(SettleRequest request, CancellationToken cancellationToken)
        {
            SettleCalls++;

            return Down
                ? throw new InvalidOperationException("The store is down.")
                : inner.SettleAsync(request, cancellationToken);
        }
    }

    private sealed record StartedEvent(DateTimeOffset At, string Reason);

    private sealed record LimitReachedEvent(DateTimeOffset StartedAt, DateTimeOffset At);

    private sealed class RecordingListener : IEnforcementOutageListener
    {
        public List<StartedEvent> Started { get; } = [];

        public List<LimitReachedEvent> LimitReached { get; } = [];

        public List<OutageSummary> Ended { get; } = [];

        public IEnumerable<object> Events => [.. Started, .. LimitReached, .. Ended];

        public void OutageStarted(DateTimeOffset at, string reason) => Started.Add(new StartedEvent(at, reason));

        public void OutageLimitReached(DateTimeOffset outageStartedAt, DateTimeOffset at) =>
            LimitReached.Add(new LimitReachedEvent(outageStartedAt, at));

        public void OutageEnded(OutageSummary summary) => Ended.Add(summary);
    }

    private sealed class ThrowingListener : IEnforcementOutageListener
    {
        public void OutageStarted(DateTimeOffset at, string reason) => throw new InvalidOperationException("alerting is down too");

        public void OutageLimitReached(DateTimeOffset outageStartedAt, DateTimeOffset at) => throw new InvalidOperationException();

        public void OutageEnded(OutageSummary summary) => throw new InvalidOperationException();
    }
}
