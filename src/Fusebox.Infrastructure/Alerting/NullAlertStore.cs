using Fusebox.Application.Alerting;

namespace Fusebox.Infrastructure.Alerting;

/// <summary>Used when alerting is off, so nothing is read or kept.</summary>
internal sealed class NullAlertStore : IAlertStore, IAlertFeed
{
    public ValueTask<AlertRecord?> FindOpenAsync(string fingerprint, CancellationToken cancellationToken) => ValueTask.FromResult<AlertRecord?>(null);

    public ValueTask<IReadOnlyList<AlertRecord>> FindOpenByKindAsync(AlertKind kind, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AlertRecord>>([]);

    public ValueTask<DateTimeOffset?> FindLastResolvedAsync(string fingerprint, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DateTimeOffset?>(null);

    public ValueTask<bool> TryOpenAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public ValueTask UpdateAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ResolveAsync(Guid alertId, DateTimeOffset at, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<PendingDelivery>> ClaimDueAsync(DateTimeOffset now, int limit, TimeSpan claimFor, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PendingDelivery>>([]);

    public ValueTask MarkSentAsync(Guid deliveryId, DateTimeOffset at, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask ScheduleRetryAsync(Guid deliveryId, DateTimeOffset nextAttemptAt, string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask MarkFailedAsync(Guid deliveryId, string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    public ValueTask<IReadOnlyList<AnomalyAlertState>> LoadAnomaliesAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AnomalyAlertState>>([]);

    public ValueTask<IReadOnlyList<BudgetBlock>> LoadBudgetBlocksAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<BudgetBlock>>([]);
}
