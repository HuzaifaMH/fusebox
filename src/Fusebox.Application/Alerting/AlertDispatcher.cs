using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Alerting;

/// <summary>
/// Sends the saved notifications that are due. A notification leaves the queue only when its channel has confirmed it,
/// and until then it is retried with growing pauses, so a channel that is down delays an alert and never loses it.
/// </summary>
/// <remarks>
/// Delivery is at least once: if a gateway instance dies after a channel accepted a notification but before it was
/// marked sent, it is sent again. That is safe because channels dedupe on the alert's fingerprint.
/// </remarks>
public sealed partial class AlertDispatcher
{
    private readonly IAlertStore _store;
    private readonly Dictionary<string, INotifier> _notifiers;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertDispatcher> _logger;

    private long _lastRoundTicks;

    public AlertDispatcher(
        IAlertStore store,
        IEnumerable<INotifier> notifiers,
        IOptions<AlertOptions> options,
        ILogger<AlertDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(notifiers);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _notifiers = notifiers.ToDictionary(n => n.Channel, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>When a round last completed, or null if none has. A dispatcher that stops completing rounds is not alive.</summary>
    public DateTimeOffset? LastRoundAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastRoundTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Sends what is due as of <paramref name="now"/>. Returns how many notifications were taken for sending.</summary>
    public async ValueTask<int> DispatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var due = await _store.ClaimDueAsync(now, _options.BatchSize, _options.ClaimDuration, cancellationToken);
        Interlocked.Exchange(ref _lastRoundTicks, now.UtcTicks);

        foreach (var delivery in due)
        {
            await DeliverAsync(delivery, now, cancellationToken);
        }

        return due.Count;
    }

    private async Task DeliverAsync(PendingDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outcome = await AttemptAsync(delivery, cancellationToken);

        switch (outcome)
        {
            case Delivered:
                await _store.MarkSentAsync(delivery.Id, now, cancellationToken);
                LogSent(delivery.Channel, delivery.Notification.Event, delivery.Notification.Fingerprint, delivery.Attempts);
                break;

            case RetryLater retry:
                // The channel's own advice, such as a rate limit's Retry-After, wins over our guess when it asks for longer.
                var wait = retry.After is { } advised && advised > AlertPolicy.Backoff(delivery.Attempts, _options)
                    ? advised
                    : AlertPolicy.Backoff(delivery.Attempts, _options);

                await _store.ScheduleRetryAsync(delivery.Id, now + wait, Truncate(retry.Reason), cancellationToken);
                LogRetrying(delivery.Channel, delivery.Notification.Fingerprint, delivery.Attempts, wait, retry.Reason);
                break;

            case Rejected rejected:
                await _store.MarkFailedAsync(delivery.Id, Truncate(rejected.Reason), cancellationToken);
                LogRejected(delivery.Channel, delivery.Notification.Fingerprint, rejected.Reason);
                break;

            default:
                break;
        }
    }

#pragma warning disable CA1031 // Whatever goes wrong in a channel, the notification stays queued and is tried again.
    private async Task<NotifyOutcome> AttemptAsync(PendingDelivery delivery, CancellationToken cancellationToken)
    {
        if (!_notifiers.TryGetValue(delivery.Channel, out var notifier))
        {
            return new Rejected($"The channel '{delivery.Channel}' is not configured.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.NotifyTimeout);

        try
        {
            return await notifier.NotifyAsync(delivery.Notification, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RetryLater($"The channel did not answer within {_options.NotifyTimeout}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new RetryLater($"The channel failed: {exception.GetType().Name}.");
        }
    }
#pragma warning restore CA1031

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];

    [LoggerMessage(EventId = 8101, Level = LogLevel.Information, Message = "Sent a {Event} for {Fingerprint} to {Channel} (attempt {Attempts})")]
    private partial void LogSent(string channel, AlertEvent @event, string fingerprint, int attempts);

    [LoggerMessage(EventId = 8102, Level = LogLevel.Warning, Message = "{Channel} did not take the notification for {Fingerprint} (attempt {Attempts}); trying again in {Wait}. {Reason}")]
    private partial void LogRetrying(string channel, string fingerprint, int attempts, TimeSpan wait, string reason);

    [LoggerMessage(EventId = 8103, Level = LogLevel.Error, Message = "{Channel} will never take the notification for {Fingerprint}, so it was given up on. {Reason}")]
    private partial void LogRejected(string channel, string fingerprint, string reason);
}
