using Fusebox.Application.Alerting;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Domain;

namespace Fusebox.Application.Tests;

internal sealed class StoredDelivery
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid AlertId { get; init; }

    public required DeliveryRequest Request { get; init; }

    public string Status { get; set; } = "pending";

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? ClaimedUntil { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>An alert store in memory that behaves like the real one, and can be told to lose a race.</summary>
internal sealed class FakeAlertStore : IAlertStore
{
    private readonly List<AlertRecord> _open = [];
    private readonly Dictionary<string, DateTimeOffset> _resolved = [];
    private readonly DateTimeOffset _startedAt;

    public FakeAlertStore(DateTimeOffset startedAt) => _startedAt = startedAt;

    public IReadOnlyList<AlertRecord> Open => _open;

    public IReadOnlyDictionary<string, DateTimeOffset> Resolved => _resolved;

    public List<StoredDelivery> Deliveries { get; } = [];

    /// <summary>Another instance opens this alert in the middle of the next open, so that open loses.</summary>
    public AlertRecord? RaceWith { get; set; }

    public void SetResolved(string fingerprint, DateTimeOffset at) => _resolved[fingerprint] = at;

    public void AddOpen(AlertRecord alert) => _open.Add(alert);

    public ValueTask<AlertRecord?> FindOpenAsync(string fingerprint, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_open.FirstOrDefault(a => a.Fingerprint == fingerprint));

    public ValueTask<IReadOnlyList<AlertRecord>> FindOpenByKindAsync(AlertKind kind, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AlertRecord>>([.. _open.Where(a => a.Kind == kind)]);

    public ValueTask<DateTimeOffset?> FindLastResolvedAsync(string fingerprint, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_resolved.TryGetValue(fingerprint, out var at) ? (DateTimeOffset?)at : null);

    public ValueTask<bool> TryOpenAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken)
    {
        if (RaceWith is { } winner)
        {
            RaceWith = null;
            _open.Add(winner);
            return ValueTask.FromResult(false);
        }

        if (_open.Any(a => a.Fingerprint == alert.Fingerprint))
        {
            return ValueTask.FromResult(false);
        }

        _open.Add(alert);
        AddDeliveries(alert.Id, deliveries, alert.OpenedAt);
        return ValueTask.FromResult(true);
    }

    public ValueTask UpdateAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken)
    {
        _open[_open.FindIndex(a => a.Id == alert.Id)] = alert;
        AddDeliveries(alert.Id, deliveries, alert.UpdatedAt);
        return ValueTask.CompletedTask;
    }

    public ValueTask ResolveAsync(Guid alertId, DateTimeOffset at, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken)
    {
        var alert = _open.Single(a => a.Id == alertId);
        _open.Remove(alert);
        _resolved[alert.Fingerprint] = at;
        AddDeliveries(alertId, deliveries, at);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<PendingDelivery>> ClaimDueAsync(DateTimeOffset now, int limit, TimeSpan claimFor, CancellationToken cancellationToken)
    {
        var due = Deliveries
            .Where(d => d.Status == "pending" && d.NextAttemptAt <= now && (d.ClaimedUntil is null || d.ClaimedUntil < now))
            .OrderBy(d => d.NextAttemptAt)
            .Take(limit)
            .ToList();

        foreach (var delivery in due)
        {
            delivery.Attempts++;
            delivery.ClaimedUntil = now + claimFor;
        }

        return ValueTask.FromResult<IReadOnlyList<PendingDelivery>>([.. due.Select(d => new PendingDelivery(d.Id, d.Request.Channel, d.Attempts, d.Request.Notification))]);
    }

    public ValueTask MarkSentAsync(Guid deliveryId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var delivery = Deliveries.Single(d => d.Id == deliveryId);
        delivery.Status = "sent";
        delivery.SentAt = at;
        return ValueTask.CompletedTask;
    }

    public ValueTask ScheduleRetryAsync(Guid deliveryId, DateTimeOffset nextAttemptAt, string reason, CancellationToken cancellationToken)
    {
        var delivery = Deliveries.Single(d => d.Id == deliveryId);
        delivery.NextAttemptAt = nextAttemptAt;
        delivery.Error = reason;
        delivery.ClaimedUntil = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkFailedAsync(Guid deliveryId, string reason, CancellationToken cancellationToken)
    {
        var delivery = Deliveries.Single(d => d.Id == deliveryId);
        delivery.Status = "failed";
        delivery.Error = reason;
        return ValueTask.CompletedTask;
    }

    private void AddDeliveries(Guid alertId, IReadOnlyList<DeliveryRequest> requests, DateTimeOffset at)
    {
        foreach (var request in requests)
        {
            Deliveries.Add(new StoredDelivery { AlertId = alertId, Request = request, NextAttemptAt = at });
        }
    }
}

internal sealed class FakeNotifier(string channel) : INotifier
{
    public string Channel { get; } = channel;

    public List<AlertNotification> Received { get; } = [];

    public Queue<NotifyOutcome> Outcomes { get; } = new();

    public Exception? Failure { get; set; }

    public bool Hang { get; set; }

    public async ValueTask<NotifyOutcome> NotifyAsync(AlertNotification notification, CancellationToken cancellationToken)
    {
        Received.Add(notification);

        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return Outcomes.Count > 0 ? Outcomes.Dequeue() : new Delivered();
    }
}

internal sealed class FakeAlertFeed : IAlertFeed
{
    public bool LockAvailable { get; set; } = true;

    public List<AnomalyAlertState> Anomalies { get; } = [];

    public List<BudgetBlock> Blocks { get; } = [];

    public async ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        if (!LockAvailable)
        {
            return false;
        }

        await work(cancellationToken);
        return true;
    }

    public ValueTask<IReadOnlyList<AnomalyAlertState>> LoadAnomaliesAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AnomalyAlertState>>([.. Anomalies]);

    public ValueTask<IReadOnlyList<BudgetBlock>> LoadBudgetBlocksAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<BudgetBlock>>([.. Blocks]);
}

internal sealed class RecordingInbox : IAlertInbox
{
    public List<QueuedAlert> Items { get; } = [];

    public bool TryPost(QueuedAlert alert)
    {
        Items.Add(alert);
        return true;
    }
}

internal static class AlertSamples
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-04T12:00:00Z");

    public static AlertSignal SampleSignal(
        string fingerprint = "anomaly:acme:openai-chat:spend",
        AlertSeverity severity = AlertSeverity.Warning,
        string revision = "detected",
        string summary = "Spend jumped.") =>
        new(
            fingerprint,
            AlertKind.Anomaly,
            severity,
            "Spend anomaly: acme on openai-chat",
            summary,
            new Dictionary<string, string> { ["Tenant"] = "acme" },
            revision,
            Now);

    public static AnomalyAlertState SampleAnomaly(
        bool isOpen = true,
        DateTimeOffset? openedAt = null,
        double peakRatio = 5,
        DiagnosisSummary? diagnosis = null,
        DateTimeOffset? closedAt = null,
        Signal signal = Detection.Signal.Spend,
        string tenant = "acme") =>
        new(
            Guid.NewGuid(),
            new SeriesKey(TenantId.Parse(tenant), "openai-chat"),
            signal,
            isOpen,
            openedAt ?? Now.AddMinutes(-5),
            closedAt,
            200_000,
            BaselineBasis.Recent,
            1_000_000,
            200_000 * peakRatio,
            peakRatio,
            diagnosis);

    public static DiagnosisSummary SampleDiagnosis(NarrationSource source = NarrationSource.Template) =>
        new("Spend rose 5x because calls per minute went up.", "Look for a retry loop.", NarrationConfidence.High, source);
}
