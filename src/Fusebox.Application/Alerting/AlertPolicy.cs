namespace Fusebox.Application.Alerting;

/// <summary>What to do with a signal, given the alert that is already open for it, if any.</summary>
public enum AlertAction
{
    /// <summary>Nothing new to say.</summary>
    Ignore,

    /// <summary>Raise a new alert.</summary>
    Open,

    /// <summary>The open alert got more severe.</summary>
    Escalate,

    /// <summary>The open alert has something new to say.</summary>
    Update,
}

/// <summary>
/// The rules that keep alerts from becoming noise. Pure, so every rule can be tested without a database or a clock.
/// </summary>
public static class AlertPolicy
{
    /// <summary>
    /// Raising the same incident again while its alert is open is the same alert, and says nothing new. A signal creates a
    /// new alert only if there is none open and the last one is not too recent. It escalates an open alert only when it is
    /// more severe, never when it is less, and updates it only when it has a new revision.
    /// </summary>
    public static AlertAction Decide(
        AlertSignal signal,
        AlertRecord? open,
        DateTimeOffset? lastResolvedAt,
        DateTimeOffset now,
        AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(options);

        if (open is null)
        {
            return lastResolvedAt is { } resolved && now - resolved < Cooldown(signal.Severity, options)
                ? AlertAction.Ignore
                : AlertAction.Open;
        }

        if (signal.Severity > open.Severity)
        {
            return AlertAction.Escalate;
        }

        return signal.Revision != open.Revision ? AlertAction.Update : AlertAction.Ignore;
    }

    /// <summary>
    /// How long after an alert resolves before the same incident may raise another. A critical alert has the shorter wait,
    /// because a second outage straight after the first is exactly what someone must be woken for.
    /// </summary>
    public static TimeSpan Cooldown(AlertSeverity severity, AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return severity == AlertSeverity.Critical ? options.CriticalCooldown : options.WarningCooldown;
    }

    /// <summary>
    /// The wait before trying a failed delivery again: it doubles with each failure, up to a ceiling. A channel that is
    /// down is retried at the ceiling's pace until it accepts the notification.
    /// </summary>
    public static TimeSpan Backoff(int attempts, AlertOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var doublings = Math.Clamp(attempts - 1, 0, 30);
        var ticks = options.FirstBackoff.Ticks * (1L << doublings);

        return ticks <= 0 || ticks > options.MaxBackoff.Ticks ? options.MaxBackoff : TimeSpan.FromTicks(ticks);
    }
}
