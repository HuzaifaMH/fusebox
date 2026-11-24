using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>The result of asking whether a request may be forwarded: either granted or denied.</summary>
public abstract record BudgetDecision
{
    private protected BudgetDecision()
    {
    }
}

/// <summary>The request may be forwarded. The reservation must be settled afterwards.</summary>
public sealed record BudgetGranted(Reservation Reservation) : BudgetDecision;

/// <summary>The request must not be forwarded.</summary>
/// <param name="Reason">Why it was denied.</param>
/// <param name="RetryAfter">When retrying could succeed, or null if retrying will not help.</param>
/// <param name="Remaining">The tenant's remaining budget in the current window.</param>
public sealed record BudgetDenied(DenialReason Reason, TimeSpan? RetryAfter, Money Remaining) : BudgetDecision;

public enum DenialReason
{
    /// <summary>The tenant has no room left in the current window.</summary>
    BudgetExceeded,

    /// <summary>The request alone could cost more than the tenant's whole budget, so waiting will not help.</summary>
    RequestExceedsBudget,

    /// <summary>The model has no price, so its cost cannot be bounded.</summary>
    UnknownModel,
}
