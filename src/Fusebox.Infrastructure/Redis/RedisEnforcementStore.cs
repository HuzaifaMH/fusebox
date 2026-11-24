using Fusebox.Application.Budgeting;
using Fusebox.Domain;
using Fusebox.Infrastructure.Enforcement;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Fusebox.Infrastructure.Redis;

/// <summary>
/// The shared <see cref="IEnforcementStore"/>. Each reserve or settle is one Lua script, which Redis
/// runs as a single indivisible step, so concurrent requests across all gateway instances cannot overshoot a budget.
/// </summary>
internal sealed class RedisEnforcementStore(IConnectionMultiplexer redis, IOptions<EnforcementOptions> options)
    : IEnforcementStore
{
    public async ValueTask<ReserveResult> TryReserveAsync(BudgetCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = options.Value;
        var window = BudgetWindow.Containing(charge.At, charge.Policy.Window);
        var ttl = (window.End - charge.At) + settings.SettlementGrace;

        var reply = await redis.GetDatabase().ScriptEvaluateAsync(
            RedisScripts.Reserve,
            new
            {
                budgetKey = BudgetKey(settings, charge.TenantId, window.Start),
                holdKey = HoldKey(settings, charge.TenantId, charge.ReservationId),
                amount = charge.Amount.MicroDollars,
                limit = charge.Policy.Limit.MicroDollars,
                ttlMs = (long)ttl.TotalMilliseconds,
            });

        var values = (long[])reply!;
        return new ReserveResult(Granted: values[0] == 1, window, Money.FromMicroDollars(values[1]));
    }

    public async ValueTask<SettleOutcome> SettleAsync(SettleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = options.Value;
        var reservation = request.Reservation;

        var reply = await redis.GetDatabase().ScriptEvaluateAsync(
            RedisScripts.Settle,
            new
            {
                budgetKey = BudgetKey(settings, reservation.TenantId, reservation.WindowStart),
                holdKey = HoldKey(settings, reservation.TenantId, reservation.Id),
                actual = request.Actual.MicroDollars,
            });

        var values = (long[])reply!;
        return values[0] == 1 ? SettleOutcome.Applied : SettleOutcome.ReservationNotFound;
    }

    // The tenant id sits in a {hash tag} so a tenant's keys always land in the same Redis Cluster slot,
    // which a multi-key script requires.
    private static RedisKey BudgetKey(EnforcementOptions settings, TenantId tenant, DateTimeOffset windowStart) =>
        $"{settings.Redis.KeyPrefix}:{{{tenant}}}:budget:{windowStart.ToUnixTimeSeconds()}";

    private static RedisKey HoldKey(EnforcementOptions settings, TenantId tenant, Guid reservationId) =>
        $"{settings.Redis.KeyPrefix}:{{{tenant}}}:hold:{reservationId:N}";
}
