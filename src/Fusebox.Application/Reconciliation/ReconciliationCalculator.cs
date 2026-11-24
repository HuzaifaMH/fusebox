using Fusebox.Domain;

namespace Fusebox.Application.Reconciliation;

/// <summary>
/// Compares what Fusebox charged with what the vendor billed. Pure arithmetic, so every rule here can be tested without a database.
/// </summary>
/// <remarks>
/// Models are matched by name, ignoring case, and never guessed at: a vendor that bills "gpt-4o-2024-08-06" while callers
/// send "gpt-4o" shows up as two lines that each fail to match, which is the honest answer. Differences smaller than the
/// tolerance are ignored, and so are periods where there is too little money to judge.
/// </remarks>
public static class ReconciliationCalculator
{
    /// <summary>Differences up to this fraction of the vendor's figure count as a match.</summary>
    public const double Tolerance = 0.02;

    /// <summary>Below this, on both sides, a drift percentage says nothing: a few cents of rounding is a large percentage.</summary>
    public static readonly Money MinimumJudgeable = Money.FromDollars(1m);

    public static ReconciliationReport Compare(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        IReadOnlyDictionary<string, Money> fuseboxByModel,
        IReadOnlyList<VendorLine> vendorLines)
    {
        ArgumentNullException.ThrowIfNull(fuseboxByModel);
        ArgumentNullException.ThrowIfNull(vendorLines);

        var fuseboxTotal = Sum(fuseboxByModel.Values);
        var vendorTotal = Sum(vendorLines.Select(l => l.Cost));
        var total = Judge(null, fuseboxTotal, vendorTotal, Status.Normal);

        // A statement with a single line for the whole period says nothing about models.
        if (vendorLines.All(l => l.Model is null))
        {
            return new ReconciliationReport(periodStart, periodEnd, total, []);
        }

        var fusebox = new Dictionary<string, Money>(fuseboxByModel, StringComparer.OrdinalIgnoreCase);
        var vendor = vendorLines.ToDictionary(l => l.Model!, l => l.Cost, StringComparer.OrdinalIgnoreCase);

        var lines = new List<ReconciliationLine>();

        foreach (var model in fusebox.Keys.Union(vendor.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var charged = fusebox.GetValueOrDefault(model);
            var billed = vendor.GetValueOrDefault(model);

            var missing = (inFusebox: fusebox.ContainsKey(model) && charged.MicroDollars > 0, onStatement: vendor.ContainsKey(model)) switch
            {
                (true, false) => Status.NotOnStatement,
                (false, true) when billed.MicroDollars > 0 => Status.NotRecordedByFusebox,
                _ => Status.Normal,
            };

            lines.Add(Judge(model, charged, billed, missing));
        }

        return new ReconciliationReport(periodStart, periodEnd, total, lines);
    }

    private static ReconciliationLine Judge(string? model, Money fusebox, Money vendor, Status missing)
    {
        double? drift = vendor.MicroDollars == 0 ? null : (double)(fusebox.MicroDollars - vendor.MicroDollars) / vendor.MicroDollars;

        if (missing == Status.NotOnStatement)
        {
            return new ReconciliationLine(model, fusebox, vendor, drift, DriftStatus.NotOnStatement, SuggestedFactor: null);
        }

        if (missing == Status.NotRecordedByFusebox)
        {
            return new ReconciliationLine(model, fusebox, vendor, drift, DriftStatus.NotRecordedByFusebox, SuggestedFactor: null);
        }

        if (fusebox < MinimumJudgeable && vendor < MinimumJudgeable)
        {
            return new ReconciliationLine(model, fusebox, vendor, drift, DriftStatus.TooSmallToJudge, SuggestedFactor: null);
        }

        if (drift is not { } d || Math.Abs(d) <= Tolerance)
        {
            return new ReconciliationLine(model, fusebox, vendor, drift, DriftStatus.Matches, SuggestedFactor: null);
        }

        double? factor = fusebox.MicroDollars > 0 ? (double)vendor.MicroDollars / fusebox.MicroDollars : null;
        return new ReconciliationLine(model, fusebox, vendor, drift, d < 0 ? DriftStatus.FuseboxUnder : DriftStatus.FuseboxOver, factor);
    }

    private static Money Sum(IEnumerable<Money> amounts) => amounts.Aggregate(Money.Zero, (sum, amount) => sum.Add(amount));

    private enum Status
    {
        Normal,
        NotOnStatement,
        NotRecordedByFusebox,
    }
}
