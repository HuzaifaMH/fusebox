using Fusebox.Application.Admin;
using Fusebox.Application.Alerting;
using Fusebox.Application.Reconciliation;
using Fusebox.Domain;

namespace Fusebox.Gateway.Admin;

/// <summary>Money in the admin API is in US dollars, because that is what people think in. Inside Fusebox it is whole micro-dollars.</summary>
internal sealed record CreateTenantRequest(string Id, string Name, decimal BudgetLimitUsd, int BudgetWindowSeconds);

internal sealed record SetBudgetRequest(decimal BudgetLimitUsd, int BudgetWindowSeconds);

/// <summary>What a tenant pays compared with list price: 0.85 is a 15% discount, 1 is list price.</summary>
internal sealed record SetPricingRequest(decimal PriceMultiplier);

internal sealed record ReconcileRequest(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, IReadOnlyList<ReconcileLineRequest> Lines);

/// <summary>One line of the vendor's statement. Leave the model out to give a total for the whole period.</summary>
internal sealed record ReconcileLineRequest(string? Model, decimal VendorCostUsd);

internal sealed record SetPriceRequest(decimal InputUsdPerMillionTokens, decimal OutputUsdPerMillionTokens, DateTimeOffset? EffectiveFrom);

internal sealed record TenantResponse(
    string Id,
    string Name,
    decimal BudgetLimitUsd,
    int BudgetWindowSeconds,
    bool Enabled,
    decimal PriceMultiplier,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static TenantResponse From(TenantSummary tenant) =>
        new(
            tenant.Id.Value,
            tenant.Name,
            tenant.Budget.Limit.ToDollars(),
            (int)tenant.Budget.Window.TotalSeconds,
            tenant.Enabled,
            Fusebox.Domain.PriceMultiplier.FromBasisPoints(tenant.PriceMultiplierBasisPoints),
            tenant.CreatedAt,
            tenant.UpdatedAt);
}

/// <summary>A new tenant and its key. The key cannot be read again, so it must be saved now.</summary>
internal sealed record CreatedTenantResponse(TenantResponse Tenant, string ApiKey);

internal sealed record RotatedKeyResponse(string TenantId, string ApiKey);

internal sealed record PriceResponse(string Model, decimal InputUsdPerMillionTokens, decimal OutputUsdPerMillionTokens, DateTimeOffset EffectiveFrom)
{
    public static PriceResponse From(PriceSummary price) =>
        new(price.Model, price.Price.InputPerMillionTokens.ToDollars(), price.Price.OutputPerMillionTokens.ToDollars(), price.EffectiveFrom);
}

internal sealed record ReconciliationLineResponse(
    string? Model,
    decimal FuseboxUsd,
    decimal VendorUsd,
    double? Drift,
    string Status,
    double? SuggestedPriceFactor)
{
    public static ReconciliationLineResponse From(ReconciliationLine line) =>
        new(line.Model, line.Fusebox.ToDollars(), line.Vendor.ToDollars(), line.Drift, line.Status.ToString(), line.SuggestedFactor);
}

internal sealed record ReconciliationResponse(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    ReconciliationLineResponse Total,
    IReadOnlyList<ReconciliationLineResponse> Models)
{
    public static ReconciliationResponse From(ReconciliationReport report) =>
        new(report.PeriodStart, report.PeriodEnd, ReconciliationLineResponse.From(report.Total), [.. report.Models.Select(ReconciliationLineResponse.From)]);
}

internal sealed record AnomalyResponse(
    Guid Id,
    string TenantId,
    string RouteId,
    string Signal,
    bool IsOpen,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    double Baseline,
    double PeakValue,
    double PeakRatio,
    DiagnosisResponse? Diagnosis)
{
    public static AnomalyResponse From(AnomalyAlertState anomaly) =>
        new(
            anomaly.Id,
            anomaly.Series.TenantId.Value,
            anomaly.Series.RouteId,
            anomaly.Signal.ToString(),
            anomaly.IsOpen,
            anomaly.OpenedAt,
            anomaly.ClosedAt,
            anomaly.Baseline,
            anomaly.PeakValue,
            anomaly.PeakRatio,
            anomaly.Diagnosis is { } d ? new DiagnosisResponse(d.Diagnosis, d.Remediation, d.Confidence.ToString(), d.Source.ToString()) : null);
}

internal sealed record DiagnosisResponse(string Explanation, string SuggestedNextStep, string Confidence, string Source);

internal sealed record AlertResponse(
    Guid Id,
    string Fingerprint,
    string Kind,
    string Severity,
    string Title,
    string Summary,
    IReadOnlyDictionary<string, string> Details,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt)
{
    public static AlertResponse From(AlertRecord alert) =>
        new(alert.Id, alert.Fingerprint, alert.Kind.ToString(), alert.Severity.ToString(), alert.Title, alert.Summary, alert.Details, alert.OpenedAt, alert.UpdatedAt);
}
