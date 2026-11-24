using Fusebox.Application.Budgeting;
using Fusebox.Domain;

namespace Fusebox.Infrastructure.Tests;

/// <summary>
/// The behaviour every <see cref="IEnforcementStore"/> must have. Each implementation runs this same suite,
/// so the in-memory store and the Redis store are held to one definition of "correct".
/// </summary>
public abstract class EnforcementStoreContract
{
    protected const long OneDollar = 1_000_000;

    protected static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-04T10:15:00Z");

    protected static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    protected abstract IEnforcementStore CreateStore();

    protected static TenantId NewTenant() => TenantId.Parse($"t{Guid.NewGuid():N}");

    protected static BudgetCharge Charge(TenantId tenant, long microDollars, DateTimeOffset? at = null, long limitMicroDollars = OneDollar) =>
        new(
            tenant,
            Guid.NewGuid(),
            Money.FromMicroDollars(microDollars),
            new BudgetPolicy(Money.FromMicroDollars(limitMicroDollars), Hour),
            at ?? Now);

    /// <summary>Reads the window's current spend by reserving nothing.</summary>
    protected static async Task<long> SpentAsync(IEnforcementStore store, TenantId tenant, DateTimeOffset? at = null, long limitMicroDollars = OneDollar)
    {
        var result = await store.TryReserveAsync(Charge(tenant, 0, at, limitMicroDollars), CancellationToken.None);
        return result.Spent.MicroDollars;
    }

    protected static Reservation ReservationFor(BudgetCharge charge, ReserveResult result) =>
        new(charge.ReservationId, charge.TenantId, result.Window.Start, charge.Amount, "model");

    [SkippableFact]
    public async Task A_reservation_that_fits_is_granted_and_counted()
    {
        var store = CreateStore();
        var tenant = NewTenant();

        var first = await store.TryReserveAsync(Charge(tenant, 400_000), CancellationToken.None);
        var second = await store.TryReserveAsync(Charge(tenant, 500_000), CancellationToken.None);

        Assert.True(first.Granted);
        Assert.Equal(400_000, first.Spent.MicroDollars);
        Assert.True(second.Granted);
        Assert.Equal(900_000, second.Spent.MicroDollars);
    }

    [SkippableFact]
    public async Task A_reservation_that_does_not_fit_is_denied_and_changes_nothing()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        await store.TryReserveAsync(Charge(tenant, 900_000), CancellationToken.None);

        var denied = await store.TryReserveAsync(Charge(tenant, 200_000), CancellationToken.None);

        Assert.False(denied.Granted);
        Assert.Equal(900_000, denied.Spent.MicroDollars);
        Assert.Equal(900_000, await SpentAsync(store, tenant));
    }

    [SkippableFact]
    public async Task The_limit_is_inclusive()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        await store.TryReserveAsync(Charge(tenant, 900_000), CancellationToken.None);

        var exact = await store.TryReserveAsync(Charge(tenant, 100_000), CancellationToken.None);

        Assert.True(exact.Granted);
        Assert.Equal(OneDollar, exact.Spent.MicroDollars);
    }

    [SkippableFact]
    public async Task Concurrent_reservations_never_exceed_the_limit()
    {
        var store = CreateStore();
        var tenant = NewTenant();

        // 200 requests of 1,000 each race for a 100,000 budget: exactly 100 may win.
        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ =>
            Task.Run(async () => await store.TryReserveAsync(
                Charge(tenant, 1_000, limitMicroDollars: 100_000),
                CancellationToken.None))));

        Assert.Equal(100, results.Count(r => r.Granted));
        Assert.Equal(100_000, await SpentAsync(store, tenant, limitMicroDollars: 100_000));
    }

    [SkippableFact]
    public async Task Settling_below_the_reservation_gives_the_difference_back()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        var charge = Charge(tenant, 600_000);
        var result = await store.TryReserveAsync(charge, CancellationToken.None);

        var outcome = await store.SettleAsync(
            new SettleRequest(ReservationFor(charge, result), Money.FromMicroDollars(100_000)),
            CancellationToken.None);

        Assert.Equal(SettleOutcome.Applied, outcome);
        Assert.Equal(100_000, await SpentAsync(store, tenant));
    }

    [SkippableFact]
    public async Task Settling_to_zero_releases_the_whole_reservation()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        var charge = Charge(tenant, 600_000);
        var result = await store.TryReserveAsync(charge, CancellationToken.None);

        await store.SettleAsync(new SettleRequest(ReservationFor(charge, result), Money.Zero), CancellationToken.None);

        Assert.Equal(0, await SpentAsync(store, tenant));
    }

    [SkippableFact]
    public async Task Settling_above_the_reservation_charges_the_overrun()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        var charge = Charge(tenant, 100_000);
        var result = await store.TryReserveAsync(charge, CancellationToken.None);

        await store.SettleAsync(
            new SettleRequest(ReservationFor(charge, result), Money.FromMicroDollars(150_000)),
            CancellationToken.None);

        Assert.Equal(150_000, await SpentAsync(store, tenant));
    }

    [SkippableFact]
    public async Task Settling_twice_changes_nothing_the_second_time()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        var charge = Charge(tenant, 600_000);
        var result = await store.TryReserveAsync(charge, CancellationToken.None);
        var request = new SettleRequest(ReservationFor(charge, result), Money.FromMicroDollars(100_000));

        var first = await store.SettleAsync(request, CancellationToken.None);
        var second = await store.SettleAsync(request, CancellationToken.None);

        Assert.Equal(SettleOutcome.Applied, first);
        Assert.Equal(SettleOutcome.ReservationNotFound, second);
        Assert.Equal(100_000, await SpentAsync(store, tenant));
    }

    [SkippableFact]
    public async Task Settling_an_unknown_reservation_reports_not_found_and_changes_nothing()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        await store.TryReserveAsync(Charge(tenant, 300_000), CancellationToken.None);
        var stranger = new Reservation(
            Guid.NewGuid(),
            tenant,
            BudgetWindow.Containing(Now, Hour).Start,
            Money.FromMicroDollars(300_000),
            "model");

        var outcome = await store.SettleAsync(new SettleRequest(stranger, Money.Zero), CancellationToken.None);

        Assert.Equal(SettleOutcome.ReservationNotFound, outcome);
        Assert.Equal(300_000, await SpentAsync(store, tenant));
    }

    [SkippableFact]
    public async Task Tenants_do_not_share_budgets()
    {
        var store = CreateStore();
        var first = NewTenant();
        var second = NewTenant();

        await store.TryReserveAsync(Charge(first, 900_000), CancellationToken.None);
        var other = await store.TryReserveAsync(Charge(second, 900_000), CancellationToken.None);

        Assert.True(other.Granted);
        Assert.Equal(900_000, await SpentAsync(store, second));
    }

    [SkippableFact]
    public async Task A_new_window_starts_from_zero()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        await store.TryReserveAsync(Charge(tenant, 900_000), CancellationToken.None);

        var nextHour = await store.TryReserveAsync(Charge(tenant, 900_000, at: Now.AddHours(1)), CancellationToken.None);

        Assert.True(nextHour.Granted);
        Assert.Equal(900_000, nextHour.Spent.MicroDollars);
    }

    [SkippableFact]
    public async Task Settling_after_the_window_rolls_over_adjusts_the_window_it_was_placed_in()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        var oldCharge = Charge(tenant, 600_000);
        var oldResult = await store.TryReserveAsync(oldCharge, CancellationToken.None);

        // A later call opens the next window before the first one finishes.
        var later = Now.AddMinutes(50);
        await store.TryReserveAsync(Charge(tenant, 300_000, at: later), CancellationToken.None);

        await store.SettleAsync(
            new SettleRequest(ReservationFor(oldCharge, oldResult), Money.FromMicroDollars(100_000)),
            CancellationToken.None);

        Assert.Equal(100_000, await SpentAsync(store, tenant, at: Now));
        Assert.Equal(300_000, await SpentAsync(store, tenant, at: later));
    }
}
