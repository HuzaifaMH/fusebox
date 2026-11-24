using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>A request to hold <paramref name="Amount"/> of a tenant's budget.</summary>
/// <param name="TenantId">The tenant to charge.</param>
/// <param name="ReservationId">A unique id for this hold.</param>
/// <param name="Amount">The worst-case cost to hold.</param>
/// <param name="Policy">The tenant's limit and window.</param>
/// <param name="At">The time of the request, which decides the budget window.</param>
/// <param name="FailureMode">What to do if the store cannot be reached. Stores themselves ignore it.</param>
public sealed record BudgetCharge(
    TenantId TenantId,
    Guid ReservationId,
    Money Amount,
    BudgetPolicy Policy,
    DateTimeOffset At,
    FailureMode FailureMode = FailureMode.FailOpenDegraded);
