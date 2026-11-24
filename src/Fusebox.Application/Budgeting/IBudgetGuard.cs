using Fusebox.Application.Llm;
using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>
/// Use case: decide whether an LLM request may be forwarded, then settle its real cost.
/// </summary>
public interface IBudgetGuard
{
    /// <summary>Prices the request's worst case and tries to hold it against the tenant's budget.</summary>
    /// <param name="tenant">The caller.</param>
    /// <param name="request">The call being made.</param>
    /// <param name="failureMode">What to do if the budget store cannot be reached.</param>
    /// <param name="cancellationToken">Cancelled if the caller goes away.</param>
    /// <exception cref="EnforcementUnavailableException">The budget cannot be verified and the call may not go unverified.</exception>
    ValueTask<BudgetDecision> ReserveAsync(
        Tenant tenant,
        LlmRequestInfo request,
        FailureMode failureMode,
        CancellationToken cancellationToken);

    /// <summary>Replaces a reservation with the real cost of the call.</summary>
    ValueTask<SettlementResult> SettleAsync(Reservation reservation, CallOutcome outcome, CancellationToken cancellationToken);
}
