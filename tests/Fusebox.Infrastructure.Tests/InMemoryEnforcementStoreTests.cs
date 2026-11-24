using Fusebox.Application.Budgeting;
using Fusebox.Domain;
using Fusebox.Infrastructure.Enforcement;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Infrastructure.Tests;

public class InMemoryEnforcementStoreTests : EnforcementStoreContract
{
    private readonly FakeTimeProvider _time = new(Now);

    protected override IEnforcementStore CreateStore() => new InMemoryEnforcementStore(
        _time,
        Options.Create(new EnforcementOptions()),
        NullLogger<InMemoryEnforcementStore>.Instance);

    [Fact]
    public async Task A_reservation_that_is_never_settled_expires_after_the_grace_period()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        var charge = Charge(tenant, 600_000);
        var result = await store.TryReserveAsync(charge, CancellationToken.None);

        // The window ends at 11:00 and the default grace is ten minutes, so by 13:00 the hold is gone.
        _time.Advance(TimeSpan.FromHours(2.75));

        var outcome = await store.SettleAsync(
            new SettleRequest(ReservationFor(charge, result), Money.Zero),
            CancellationToken.None);

        Assert.Equal(SettleOutcome.ReservationNotFound, outcome);
    }

    [Fact]
    public async Task A_reservation_can_still_be_settled_shortly_after_its_window_ends()
    {
        var store = CreateStore();
        var tenant = NewTenant();
        var charge = Charge(tenant, 600_000);
        var result = await store.TryReserveAsync(charge, CancellationToken.None);

        // 11:05 is after the window closed but inside the ten-minute grace period.
        _time.Advance(TimeSpan.FromMinutes(50));

        var outcome = await store.SettleAsync(
            new SettleRequest(ReservationFor(charge, result), Money.Zero),
            CancellationToken.None);

        Assert.Equal(SettleOutcome.Applied, outcome);
    }
}
