namespace Fusebox.Application.Alerting;

public enum AlertSeverity
{
    /// <summary>Worth knowing, nobody needs to act.</summary>
    Info,

    /// <summary>Someone should look soon. Goes to chat.</summary>
    Warning,

    /// <summary>Someone should look now. Pages the on-call engineer.</summary>
    Critical,
}

public enum AlertKind
{
    Anomaly,
    BudgetExhausted,
    EnforcementDegraded,
    EnforcementFailClosed,

    /// <summary>A vendor kept failing, so Fusebox cut it off.</summary>
    UpstreamUnavailable,
}

/// <summary>What a notification tells its reader.</summary>
public enum AlertEvent
{
    /// <summary>A new alert.</summary>
    Trigger,

    /// <summary>The alert got more severe.</summary>
    Escalate,

    /// <summary>The alert has new information, such as a diagnosis.</summary>
    Update,

    /// <summary>The alert's cause has gone.</summary>
    Resolve,
}

/// <summary>Something that deserves an alert, as raised by a source such as the anomaly detector.</summary>
/// <param name="Fingerprint">Names the incident. Raising the same fingerprint again while it is open is the same alert, not a new one.</param>
/// <param name="Kind">What sort of incident it is.</param>
/// <param name="Severity">How urgent it is.</param>
/// <param name="Title">A short headline.</param>
/// <param name="Summary">What is happening, in a sentence or two.</param>
/// <param name="Details">Labelled facts for the reader, such as the tenant, the route and the numbers.</param>
/// <param name="Revision">Changes when the alert has something new to say, for example once a diagnosis is available. An alert is updated only when this changes.</param>
/// <param name="At">When it was raised.</param>
public sealed record AlertSignal(
    string Fingerprint,
    AlertKind Kind,
    AlertSeverity Severity,
    string Title,
    string Summary,
    IReadOnlyDictionary<string, string> Details,
    string Revision,
    DateTimeOffset At);

/// <summary>An alert that is open: raised and not yet resolved.</summary>
public sealed record AlertRecord(
    Guid Id,
    string Fingerprint,
    AlertKind Kind,
    AlertSeverity Severity,
    string Title,
    string Summary,
    IReadOnlyDictionary<string, string> Details,
    string Revision,
    DateTimeOffset OpenedAt,
    DateTimeOffset UpdatedAt);

/// <summary>What a channel is asked to deliver.</summary>
/// <param name="Event">What this notification tells the reader.</param>
/// <param name="AlertId">The alert it is about.</param>
/// <param name="Fingerprint">The alert's fingerprint, which channels use as a dedupe key so a repeat does not make a second incident.</param>
/// <param name="Kind">What sort of incident.</param>
/// <param name="Severity">How urgent, at the time of this notification.</param>
/// <param name="Title">A short headline.</param>
/// <param name="Summary">What is happening.</param>
/// <param name="Details">Labelled facts.</param>
/// <param name="OpenedAt">When the alert was first raised.</param>
/// <param name="At">When this notification was made.</param>
/// <param name="Note">Extra words, such as what happened while the problem lasted, on a resolve.</param>
public sealed record AlertNotification(
    AlertEvent Event,
    Guid AlertId,
    string Fingerprint,
    AlertKind Kind,
    AlertSeverity Severity,
    string Title,
    string Summary,
    IReadOnlyDictionary<string, string> Details,
    DateTimeOffset OpenedAt,
    DateTimeOffset At,
    string? Note = null);

/// <summary>A notification to be sent to one channel. Saved with the alert, so nothing is lost if sending fails.</summary>
public sealed record DeliveryRequest(string Channel, string Revision, AlertNotification Notification)
{
    public AlertEvent Event => Notification.Event;

    public AlertSeverity Severity => Notification.Severity;
}

/// <summary>A saved notification that is due to be sent.</summary>
public sealed record PendingDelivery(Guid Id, string Channel, int Attempts, AlertNotification Notification);

/// <summary>What happened when a channel was asked to deliver.</summary>
public abstract record NotifyOutcome
{
    private protected NotifyOutcome()
    {
    }
}

/// <summary>The channel accepted it.</summary>
public sealed record Delivered : NotifyOutcome;

/// <summary>The channel could not take it now, so try again. <paramref name="After"/> is the channel's own advice, if it gave any.</summary>
public sealed record RetryLater(string Reason, TimeSpan? After = null) : NotifyOutcome;

/// <summary>The channel will never accept it, for example because of a wrong key, so retrying is pointless.</summary>
public sealed record Rejected(string Reason) : NotifyOutcome;
