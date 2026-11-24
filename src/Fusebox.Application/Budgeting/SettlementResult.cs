using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>The result of settling a reservation.</summary>
/// <param name="Charged">The real cost charged against the budget.</param>
/// <param name="Applied">False if the reservation was no longer known, so nothing was changed.</param>
public readonly record struct SettlementResult(Money Charged, bool Applied);
