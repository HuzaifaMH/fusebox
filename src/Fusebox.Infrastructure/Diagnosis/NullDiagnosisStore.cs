using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;

namespace Fusebox.Infrastructure.Diagnosis;

/// <summary>Used when diagnosis is off, so nothing is read or kept.</summary>
internal sealed class NullDiagnosisStore : IDiagnosisStore, IEvidenceSource
{
    public ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<IReadOnlyList<AnomalyToDiagnose>> LoadUndiagnosedAsync(DateTimeOffset openedSince, int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AnomalyToDiagnose>>([]);

    public ValueTask<int> CountLlmNarrationsAsync(DateTimeOffset since, CancellationToken cancellationToken) => ValueTask.FromResult(0);

    public ValueTask SaveAsync(DiagnosisRecord record, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<PeriodTotals> LoadPeriodAsync(SeriesKey series, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PeriodTotals.Empty((int)(periodEnd - periodStart).TotalMinutes));
}
