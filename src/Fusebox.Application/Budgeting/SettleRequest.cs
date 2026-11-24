using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>A request to replace a reservation with the real cost of the call.</summary>
/// <param name="Reservation">The hold to settle.</param>
/// <param name="Actual">The real cost. Zero releases the hold completely.</param>
public sealed record SettleRequest(Reservation Reservation, Money Actual);
