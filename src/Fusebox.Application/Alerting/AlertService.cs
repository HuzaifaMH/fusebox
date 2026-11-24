using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Alerting;

/// <summary>
/// Turns signals into saved alerts and saved notifications. Everything is written first and sent later, so an alert
/// that is raised is never lost to a restart or a channel that happens to be down.
/// </summary>
public sealed partial class AlertService
{
    private readonly IAlertStore _store;
    private readonly AlertRouter _router;
    private readonly AlertOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        IAlertStore store,
        AlertRouter router,
        IOptions<AlertOptions> options,
        TimeProvider time,
        ILogger<AlertService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _router = router;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Raises a signal. Depending on what is already open it starts a new alert, escalates or updates the open one, or does
    /// nothing at all, which is the usual answer for an incident that keeps being reported.
    /// </summary>
    public async ValueTask RaiseAsync(AlertSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // If two gateway instances open the same incident at once, the loser finds the winner's alert on the second go.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = _time.GetUtcNow();
            var open = await _store.FindOpenAsync(signal.Fingerprint, cancellationToken);
            var lastResolved = open is null ? await _store.FindLastResolvedAsync(signal.Fingerprint, cancellationToken) : null;

            switch (AlertPolicy.Decide(signal, open, lastResolved, now, _options))
            {
                case AlertAction.Open:
                    var alert = new AlertRecord(
                        Guid.CreateVersion7(now),
                        signal.Fingerprint,
                        signal.Kind,
                        signal.Severity,
                        signal.Title,
                        signal.Summary,
                        signal.Details,
                        signal.Revision,
                        now,
                        now);

                    if (await _store.TryOpenAsync(alert, Deliveries(alert, AlertEvent.Trigger, now, note: null), cancellationToken))
                    {
                        LogOpened(alert.Fingerprint, alert.Severity);
                        return;
                    }

                    continue;

                case AlertAction.Escalate:
                    var escalated = open! with
                    {
                        Severity = signal.Severity,
                        Title = signal.Title,
                        Summary = signal.Summary,
                        Details = signal.Details,
                        Revision = signal.Revision,
                        UpdatedAt = now,
                    };

                    await _store.UpdateAsync(escalated, Deliveries(escalated, AlertEvent.Escalate, now, note: null), cancellationToken);
                    LogEscalated(escalated.Fingerprint, open!.Severity, escalated.Severity);
                    return;

                case AlertAction.Update:
                    var updated = open! with { Summary = signal.Summary, Details = signal.Details, Revision = signal.Revision, UpdatedAt = now };

                    await _store.UpdateAsync(updated, Deliveries(updated, AlertEvent.Update, now, note: null), cancellationToken);
                    LogUpdated(updated.Fingerprint, updated.Revision);
                    return;

                default:
                    return;
            }
        }
    }

    /// <summary>Resolves the open alert for an incident, if there is one, and tells the channels it went to.</summary>
    public async ValueTask ResolveAsync(string fingerprint, DateTimeOffset at, string? note, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        var open = await _store.FindOpenAsync(fingerprint, cancellationToken);

        if (open is null)
        {
            return;
        }

        await _store.ResolveAsync(open.Id, at, Deliveries(open, AlertEvent.Resolve, at, note), cancellationToken);
        LogResolved(fingerprint);
    }

    private List<DeliveryRequest> Deliveries(AlertRecord alert, AlertEvent alertEvent, DateTimeOffset at, string? note)
    {
        var channels = _router.ChannelsFor(alert.Severity);

        if (channels.Count == 0)
        {
            LogNoChannel(alert.Fingerprint);
        }

        var notification = new AlertNotification(
            alertEvent,
            alert.Id,
            alert.Fingerprint,
            alert.Kind,
            alert.Severity,
            alert.Title,
            alert.Summary,
            alert.Details,
            alert.OpenedAt,
            at,
            note);

        // A resolve is one notification per channel however the alert changed before it, so it has a revision of its own.
        var revision = alertEvent == AlertEvent.Resolve ? "resolved" : alert.Revision;

        return [.. channels.Select(channel => new DeliveryRequest(channel, revision, notification))];
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Warning, Message = "Alert opened for {Fingerprint} ({Severity})")]
    private partial void LogOpened(string fingerprint, AlertSeverity severity);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Warning, Message = "Alert {Fingerprint} escalated from {From} to {To}")]
    private partial void LogEscalated(string fingerprint, AlertSeverity from, AlertSeverity to);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Information, Message = "Alert {Fingerprint} updated ({Revision})")]
    private partial void LogUpdated(string fingerprint, string revision);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Information, Message = "Alert {Fingerprint} resolved")]
    private partial void LogResolved(string fingerprint);

    [LoggerMessage(EventId = 8005, Level = LogLevel.Error, Message = "No alert channel is available for {Fingerprint}, so nobody will be told. Configure PagerDuty, Slack or a webhook")]
    private partial void LogNoChannel(string fingerprint);
}
