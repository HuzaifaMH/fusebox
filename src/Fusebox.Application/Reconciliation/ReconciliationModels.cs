using Fusebox.Domain;

namespace Fusebox.Application.Reconciliation;

/// <summary>One line of what the vendor says it billed: a model, or the whole period when <paramref name="Model"/> is null.</summary>
public sealed record VendorLine(string? Model, Money Cost);

/// <summary>A vendor's statement for a period, to be compared with what Fusebox charged.</summary>
/// <param name="PeriodStart">Start of the period, on the hour, UTC.</param>
/// <param name="PeriodEnd">End of the period (not included), on the hour, UTC.</param>
/// <param name="Lines">Either one line for the whole period, or one line per model. Not a mix.</param>
public sealed record ReconciliationRequest(DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, IReadOnlyList<VendorLine> Lines);

public enum DriftStatus
{
    /// <summary>Fusebox and the vendor agree within the tolerance.</summary>
    Matches,

    /// <summary>Fusebox charged less than the vendor billed. Prices are probably too low.</summary>
    FuseboxUnder,

    /// <summary>Fusebox charged more than the vendor billed. Prices are probably too high, or calls with unreadable usage were charged at their reservation.</summary>
    FuseboxOver,

    /// <summary>Too little money on either side to say anything useful.</summary>
    TooSmallToJudge,

    /// <summary>Fusebox charged for this model but the statement has no line for it. The statement may use a different name for it.</summary>
    NotOnStatement,

    /// <summary>The statement bills this model but Fusebox recorded nothing for it. Calls may be reaching the vendor around Fusebox.</summary>
    NotRecordedByFusebox,
}

/// <summary>Fusebox against the vendor for one model, or for the whole period.</summary>
/// <param name="Model">The model, or null for the whole period.</param>
/// <param name="Fusebox">What Fusebox charged.</param>
/// <param name="Vendor">What the vendor billed.</param>
/// <param name="Drift">How far Fusebox is from the vendor as a fraction of the vendor's figure: -0.03 is 3% under. Null when the vendor billed nothing.</param>
/// <param name="Status">What to make of it.</param>
/// <param name="SuggestedFactor">What to multiply Fusebox's prices by to match the vendor, when they do not match. Only a suggestion.</param>
public sealed record ReconciliationLine(
    string? Model,
    Money Fusebox,
    Money Vendor,
    double? Drift,
    DriftStatus Status,
    double? SuggestedFactor);

public sealed record ReconciliationReport(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    ReconciliationLine Total,
    IReadOnlyList<ReconciliationLine> Models);

/// <summary>Port: what Fusebox charged in a period, per model, from the recorded totals.</summary>
public interface IUsageCostReader
{
    /// <param name="periodStart">On the hour.</param>
    /// <param name="periodEnd">On the hour, not included.</param>
    ValueTask<IReadOnlyDictionary<string, Money>> CostByModelAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken);
}
