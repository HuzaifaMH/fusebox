using System.Collections.Concurrent;
using Fusebox.Application.Budgeting;
using Fusebox.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Enforcement;

/// <summary>
/// A single-process <see cref="IEnforcementStore"/>. Correct and atomic within one process,
/// but separate gateway instances would each keep their own ledger, so use Redis when running more than one.
/// </summary>
internal sealed partial class InMemoryEnforcementStore : IEnforcementStore
{
    private readonly ConcurrentDictionary<TenantId, TenantLedger> _ledgers = new();
    private readonly TimeProvider _time;
    private readonly IOptions<EnforcementOptions> _options;
    private readonly ILogger<InMemoryEnforcementStore> _logger;

    public InMemoryEnforcementStore(
        TimeProvider time,
        IOptions<EnforcementOptions> options,
        ILogger<InMemoryEnforcementStore> logger)
    {
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Warns that this ledger is not shared. Called when it is the real store, not when it is the
    /// local fallback behind the shared one, where being private is the point.
    /// </summary>
    public void WarnNotShared() => LogSingleInstanceOnly();

    public ValueTask<ReserveResult> TryReserveAsync(BudgetCharge charge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(charge);

        var ledger = _ledgers.GetOrAdd(charge.TenantId, static _ => new TenantLedger());
        return ValueTask.FromResult(ledger.TryReserve(charge, _options.Value.SettlementGrace));
    }

    public ValueTask<SettleOutcome> SettleAsync(SettleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = _ledgers.TryGetValue(request.Reservation.TenantId, out var ledger)
            ? ledger.Settle(request, _time.GetUtcNow())
            : SettleOutcome.ReservationNotFound;

        return ValueTask.FromResult(outcome);
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "Using the in-memory enforcement store. Budgets are not shared between gateway instances; use Redis in production.")]
    private partial void LogSingleInstanceOnly();

    private sealed class TenantLedger
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<DateTimeOffset, Bucket> _buckets = [];
        private readonly Dictionary<Guid, Hold> _holds = [];

        public ReserveResult TryReserve(BudgetCharge charge, TimeSpan grace)
        {
            var window = BudgetWindow.Containing(charge.At, charge.Policy.Window);
            var expiresAt = window.End + grace;
            var amount = charge.Amount.MicroDollars;

            lock (_gate)
            {
                Prune(charge.At);

                if (!_buckets.TryGetValue(window.Start, out var bucket))
                {
                    bucket = new Bucket(expiresAt);
                    _buckets[window.Start] = bucket;
                }

                if (bucket.SpentMicroDollars + amount > charge.Policy.Limit.MicroDollars)
                {
                    return new ReserveResult(Granted: false, window, Money.FromMicroDollars(bucket.SpentMicroDollars));
                }

                bucket.SpentMicroDollars += amount;
                _holds[charge.ReservationId] = new Hold(window.Start, amount, expiresAt);

                return new ReserveResult(Granted: true, window, Money.FromMicroDollars(bucket.SpentMicroDollars));
            }
        }

        public SettleOutcome Settle(SettleRequest request, DateTimeOffset now)
        {
            lock (_gate)
            {
                Prune(now);

                if (!_holds.Remove(request.Reservation.Id, out var hold))
                {
                    return SettleOutcome.ReservationNotFound;
                }

                if (_buckets.TryGetValue(hold.WindowStart, out var bucket))
                {
                    bucket.SpentMicroDollars = Math.Max(
                        0,
                        bucket.SpentMicroDollars - hold.AmountMicroDollars + request.Actual.MicroDollars);
                }

                return SettleOutcome.Applied;
            }
        }

        private void Prune(DateTimeOffset now)
        {
            foreach (var (start, bucket) in _buckets)
            {
                if (bucket.ExpiresAt <= now)
                {
                    _buckets.Remove(start);
                }
            }

            foreach (var (id, hold) in _holds)
            {
                if (hold.ExpiresAt <= now)
                {
                    _holds.Remove(id);
                }
            }
        }

        private sealed class Bucket(DateTimeOffset expiresAt)
        {
            public DateTimeOffset ExpiresAt { get; } = expiresAt;

            public long SpentMicroDollars { get; set; }
        }

        private readonly record struct Hold(DateTimeOffset WindowStart, long AmountMicroDollars, DateTimeOffset ExpiresAt);
    }
}
