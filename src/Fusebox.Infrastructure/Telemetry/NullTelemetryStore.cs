using Fusebox.Application.Telemetry;

namespace Fusebox.Infrastructure.Telemetry;

/// <summary>Used when telemetry is switched off: nothing is kept, so there is nothing to total or drop.</summary>
internal sealed class NullTelemetryStore : ITelemetryStore
{
    public ValueTask WriteAsync(IReadOnlyList<RequestRecord> batch, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<bool> RollUpAsync(RollupGranularity granularity, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);

    public ValueTask<int> DropRawBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => ValueTask.FromResult(0);
}
