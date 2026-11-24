using Fusebox.Application.Detection;

namespace Fusebox.Application.Diagnosis;

/// <summary>An anomaly that has not been diagnosed yet.</summary>
public sealed record AnomalyToDiagnose(Guid AnomalyId, SeriesKey Series, Signal Signal, DateTimeOffset OpenedAt);

/// <summary>An anomaly's diagnosis: the evidence found and the explanation written from it.</summary>
public sealed record DiagnosisRecord(Guid AnomalyId, DateTimeOffset DiagnosedAt, Evidence Evidence, Narration Narration);

/// <summary>Port: the totals the evidence engine compares.</summary>
public interface IEvidenceSource
{
    /// <summary>One series' totals per model over [<paramref name="periodStart"/>, <paramref name="periodEnd"/>).</summary>
    ValueTask<PeriodTotals> LoadPeriodAsync(SeriesKey series, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken);
}

/// <summary>Port: which anomalies need diagnosing, and where diagnoses are kept.</summary>
public interface IDiagnosisStore
{
    /// <summary>
    /// Runs <paramref name="work"/> unless another gateway instance is already diagnosing, in which case it returns
    /// false without running it.
    /// </summary>
    ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);

    /// <summary>Anomalies opened at or after <paramref name="openedSince"/> that have no diagnosis yet, oldest first.</summary>
    ValueTask<IReadOnlyList<AnomalyToDiagnose>> LoadUndiagnosedAsync(DateTimeOffset openedSince, int limit, CancellationToken cancellationToken);

    /// <summary>How many diagnoses written by an LLM were saved at or after <paramref name="since"/>.</summary>
    ValueTask<int> CountLlmNarrationsAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Saves a diagnosis. Saving one for an anomaly that already has one changes nothing.</summary>
    ValueTask SaveAsync(DiagnosisRecord record, CancellationToken cancellationToken);
}
