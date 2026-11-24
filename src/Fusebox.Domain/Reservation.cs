namespace Fusebox.Domain;

/// <summary>
/// A hold placed on a tenant's budget before a call is forwarded, sized for the worst case.
/// It is settled to the real cost once the call finishes.
/// </summary>
/// <param name="Id">Unique id, which also makes settlement idempotent.</param>
/// <param name="TenantId">The tenant whose budget is held.</param>
/// <param name="WindowStart">The budget window the hold was placed in. Settlement must adjust the same window.</param>
/// <param name="Reserved">The amount held.</param>
/// <param name="Model">The model the call was priced for.</param>
public sealed record Reservation(Guid Id, TenantId TenantId, DateTimeOffset WindowStart, Money Reserved, string Model);
