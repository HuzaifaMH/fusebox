namespace Fusebox.Domain;

/// <summary>An owner of a budget whose calls pass through Fusebox.</summary>
public sealed record Tenant(TenantId Id, string Name, BudgetPolicy Budget)
{
    /// <summary>What this tenant pays compared with list price. Defaults to list price.</summary>
    public TenantPricing Pricing { get; init; } = TenantPricing.List;
}
