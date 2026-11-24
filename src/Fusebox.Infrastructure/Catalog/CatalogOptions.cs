namespace Fusebox.Infrastructure.Catalog;

/// <summary>
/// Tenants and model prices read from configuration. This is the first source of that data,
/// behind the same ports a database-backed source will implement later.
/// </summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Fusebox:Catalog";

    /// <summary>Tenants keyed by tenant id.</summary>
    public Dictionary<string, TenantOptions> Tenants { get; set; } = [];

    /// <summary>Model prices keyed by the model name clients send.</summary>
    public Dictionary<string, ModelPriceOptions> Models { get; set; } = [];
}

public sealed class TenantOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The lowercase hex SHA-256 of the tenant's API key. Raw keys are never configured.</summary>
    public string ApiKeyHash { get; set; } = string.Empty;

    public decimal BudgetLimitUsd { get; set; }

    public TimeSpan BudgetWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>What this tenant pays compared with list price: 0.85 is a 15% discount. 1 is list price.</summary>
    public decimal PriceMultiplier { get; set; } = 1m;

    /// <summary>Negotiated rates that replace the list price for a model, keyed by the model name clients send.</summary>
    public Dictionary<string, ModelPriceOptions> ModelPrices { get; set; } = [];
}

public sealed class ModelPriceOptions
{
    public decimal InputUsdPerMillionTokens { get; set; }

    public decimal OutputUsdPerMillionTokens { get; set; }
}
