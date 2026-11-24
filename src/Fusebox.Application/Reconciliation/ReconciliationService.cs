using Microsoft.Extensions.Logging;

namespace Fusebox.Application.Reconciliation;

/// <summary>
/// Checks Fusebox's totals against a vendor statement someone supplies, and reports where they differ. It changes nothing:
/// whether to adjust prices is a decision for a person, who can see the invoice and the contract.
/// </summary>
/// <remarks>
/// The statement is typed in or imported rather than fetched, because vendors' usage and billing APIs differ, change, and often
/// need separate admin credentials. A job that fetches them can feed this same check.
/// </remarks>
public sealed partial class ReconciliationService
{
    /// <summary>The most recent hours are left out: their totals can still change while records are being saved.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromHours(3);

    private const int MaxLines = 500;
    private static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    private readonly IUsageCostReader _reader;
    private readonly TimeProvider _time;
    private readonly ILogger<ReconciliationService> _logger;

    public ReconciliationService(IUsageCostReader reader, TimeProvider time, ILogger<ReconciliationService> logger)
    {
        _reader = reader;
        _time = time;
        _logger = logger;
    }

    /// <exception cref="ArgumentException">The period or the statement cannot be compared. The message says why and is safe to show.</exception>
    public async ValueTask<ReconciliationReport> ReconcileAsync(ReconciliationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var fusebox = await _reader.CostByModelAsync(request.PeriodStart, request.PeriodEnd, cancellationToken);
        var report = ReconciliationCalculator.Compare(request.PeriodStart, request.PeriodEnd, fusebox, request.Lines);

        LogReconciled(request.PeriodStart, request.PeriodEnd, report.Total.Status, report.Total.Drift ?? 0);
        return report;
    }

    private void Validate(ReconciliationRequest request)
    {
        if (!OnTheHour(request.PeriodStart) || !OnTheHour(request.PeriodEnd))
        {
            throw new ArgumentException("The period must start and end on the hour, in UTC, because Fusebox's totals are kept per hour.");
        }

        if (request.PeriodEnd <= request.PeriodStart || request.PeriodEnd - request.PeriodStart > MaxPeriod)
        {
            throw new ArgumentException("The period must end after it starts, and last at most 366 days.");
        }

        if (request.PeriodEnd > _time.GetUtcNow() - SettleDelay)
        {
            throw new ArgumentException($"The period must end at least {SettleDelay.TotalHours:0} hours ago, so that every call in it has been recorded.");
        }

        if (request.Lines.Count is 0 or > MaxLines)
        {
            throw new ArgumentException($"A statement has 1 to {MaxLines} lines.");
        }

        if (request.Lines.Any(l => l.Model is not null && string.IsNullOrWhiteSpace(l.Model)))
        {
            throw new ArgumentException("A statement line has a blank model name. Leave the model out to give a total for the whole period.");
        }

        var models = request.Lines.Count(l => l.Model is not null);
        if (models != 0 && models != request.Lines.Count)
        {
            throw new ArgumentException("Give either one total for the whole period, or a line for every model, not a mix.");
        }

        if (models == 0 && request.Lines.Count != 1)
        {
            throw new ArgumentException("A statement without models is a single total for the period.");
        }

        var duplicate = request.Lines.Where(l => l.Model is not null).GroupBy(l => l.Model, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"The model '{duplicate.Key}' appears on more than one line.");
        }
    }

    private static bool OnTheHour(DateTimeOffset instant) => instant.Offset == TimeSpan.Zero && instant.UtcTicks % TimeSpan.TicksPerHour == 0;

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Reconciled {PeriodStart:o} to {PeriodEnd:o}: {Status}, Fusebox differs from the vendor by {Drift:P1}")]
    private partial void LogReconciled(DateTimeOffset periodStart, DateTimeOffset periodEnd, DriftStatus status, double drift);
}
