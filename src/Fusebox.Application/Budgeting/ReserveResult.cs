using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>The outcome of trying to hold part of a budget.</summary>
/// <param name="Granted">True if the amount fit and was held.</param>
/// <param name="Window">The budget window the check applied to.</param>
/// <param name="Spent">
/// The window's spend after the hold if granted, or the spend that left no room for it if denied.
/// </param>
public readonly record struct ReserveResult(bool Granted, BudgetWindow Window, Money Spent);
