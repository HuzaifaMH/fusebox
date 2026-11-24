using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>
/// Port: the shared, atomic ledger of how much each tenant has spent in the current window.
/// </summary>
/// <remarks>
/// Implementations must make <see cref="TryReserveAsync"/> and <see cref="SettleAsync"/> atomic,
/// so concurrent requests can never collectively exceed a budget.
/// </remarks>
public interface IEnforcementStore
{
    /// <summary>
    /// Atomically checks whether <see cref="BudgetCharge.Amount"/> fits in the tenant's remaining budget and,
    /// if so, adds it to the window's spend and records the reservation.
    /// </summary>
    ValueTask<ReserveResult> TryReserveAsync(BudgetCharge charge, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a reservation with the real cost. Idempotent: settling an unknown or already-settled
    /// reservation changes nothing and reports <see cref="SettleOutcome.ReservationNotFound"/>.
    /// </summary>
    ValueTask<SettleOutcome> SettleAsync(SettleRequest request, CancellationToken cancellationToken);
}
