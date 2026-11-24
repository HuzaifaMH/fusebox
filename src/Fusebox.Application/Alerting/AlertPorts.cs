using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Domain;

namespace Fusebox.Application.Alerting;

/// <summary>Port: one way of reaching a person, such as PagerDuty or Slack.</summary>
public interface INotifier
{
    /// <summary>The channel's name, as used in routing.</summary>
    string Channel { get; }

    /// <summary>
    /// Delivers a notification. Returns what happened instead of throwing for the expected failures, so the caller can tell a
    /// channel that is down (retry) from one that will never accept it (give up).
    /// </summary>
    ValueTask<NotifyOutcome> NotifyAsync(AlertNotification notification, CancellationToken cancellationToken);
}

/// <summary>Port: where alerts and the notifications waiting to be sent are kept.</summary>
public interface IAlertStore
{
    /// <summary>The open alert for an incident, or null.</summary>
    ValueTask<AlertRecord?> FindOpenAsync(string fingerprint, CancellationToken cancellationToken);

    /// <summary>All open alerts of one kind.</summary>
    ValueTask<IReadOnlyList<AlertRecord>> FindOpenByKindAsync(AlertKind kind, CancellationToken cancellationToken);

    /// <summary>When an incident's alert last resolved, or null if it never has.</summary>
    ValueTask<DateTimeOffset?> FindLastResolvedAsync(string fingerprint, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a new alert together with its notifications, in one step. Returns false if another gateway instance opened an
    /// alert for the same incident first, in which case nothing is saved.
    /// </summary>
    ValueTask<bool> TryOpenAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken);

    /// <summary>Saves a change to an open alert together with its notifications, in one step.</summary>
    ValueTask UpdateAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken);

    /// <summary>Marks an alert resolved and saves its notifications, in one step.</summary>
    ValueTask ResolveAsync(Guid alertId, DateTimeOffset at, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken);

    /// <summary>
    /// Takes notifications that are due for sending, holding them for <paramref name="claimFor"/> so no other gateway
    /// instance sends them too. If the holder dies, they come due again when the hold ends.
    /// </summary>
    ValueTask<IReadOnlyList<PendingDelivery>> ClaimDueAsync(DateTimeOffset now, int limit, TimeSpan claimFor, CancellationToken cancellationToken);

    ValueTask MarkSentAsync(Guid deliveryId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Puts a notification back to be tried again at <paramref name="nextAttemptAt"/>.</summary>
    ValueTask ScheduleRetryAsync(Guid deliveryId, DateTimeOffset nextAttemptAt, string reason, CancellationToken cancellationToken);

    /// <summary>Gives up on a notification the channel will never accept. It stays on record, with why.</summary>
    ValueTask MarkFailedAsync(Guid deliveryId, string reason, CancellationToken cancellationToken);
}

/// <summary>A diagnosis, in the parts an alert needs.</summary>
public sealed record DiagnosisSummary(
    string Diagnosis,
    string Remediation,
    NarrationConfidence Confidence,
    NarrationSource Source);

/// <summary>An anomaly as the alerting sees it.</summary>
public sealed record AnomalyAlertState(
    Guid Id,
    SeriesKey Series,
    Signal Signal,
    bool IsOpen,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    double Baseline,
    BaselineBasis Basis,
    double OpenedValue,
    double PeakValue,
    double PeakRatio,
    DiagnosisSummary? Diagnosis);

/// <summary>A tenant whose calls were blocked because its budget was used up.</summary>
public sealed record BudgetBlock(TenantId TenantId, long BlockedCalls, DateTimeOffset LastBlockedAt);

/// <summary>Port: what is wrong right now, for the sources that find out by looking.</summary>
public interface IAlertFeed
{
    /// <summary>Runs <paramref name="work"/> unless another gateway instance is already doing so, in which case it returns false.</summary>
    ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);

    /// <summary>Anomalies opened or closed at or after <paramref name="since"/>, with their diagnoses.</summary>
    ValueTask<IReadOnlyList<AnomalyAlertState>> LoadAnomaliesAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Tenants with calls blocked for budget at or after <paramref name="since"/>.</summary>
    ValueTask<IReadOnlyList<BudgetBlock>> LoadBudgetBlocksAsync(DateTimeOffset since, CancellationToken cancellationToken);
}
