using System.Diagnostics.Metrics;
using Fusebox.Application.Alerting;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Alerting.Notifiers;

/// <summary>How alert delivery itself is doing. A delivery that keeps failing is otherwise silent until someone asks why nobody was paged.</summary>
internal sealed class AlertMetrics : IDisposable
{
    public const string MeterName = "Fusebox.Alerts";

    private readonly Meter _meter;

    public AlertMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);
        Deliveries = _meter.CreateCounter<long>("fusebox.alerts.deliveries", "{notification}", "Notifications handed to a channel, by channel and result.");
    }

    public Counter<long> Deliveries { get; }

    public void Dispose() => _meter.Dispose();
}

/// <summary>Counts what happens to every notification sent through a channel.</summary>
internal sealed class MeteredNotifier(INotifier inner, AlertMetrics metrics) : INotifier
{
    public string Channel => inner.Channel;

    public async ValueTask<NotifyOutcome> NotifyAsync(AlertNotification notification, CancellationToken cancellationToken)
    {
        var result = "cancelled";

        try
        {
            var outcome = await inner.NotifyAsync(notification, cancellationToken);
            result = outcome switch
            {
                Delivered => "delivered",
                RetryLater => "retry",
                _ => "rejected",
            };

            return outcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = "error";
            throw;
        }
        finally
        {
            metrics.Deliveries.Add(
                1,
                new KeyValuePair<string, object?>("channel", Channel),
                new KeyValuePair<string, object?>("result", result));
        }
    }
}

/// <summary>Builds the channels that are configured, and owns the HTTP client they share.</summary>
internal sealed class NotifierCatalog : IDisposable
{
    private readonly HttpClient _client = new();

    public NotifierCatalog(IOptions<AlertOptions> options, AlertMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = options.Value;
        var notifiers = new List<INotifier>();

        if (settings.PagerDuty.IsConfigured)
        {
            notifiers.Add(new MeteredNotifier(new PagerDutyNotifier(_client, options), metrics));
        }

        if (settings.Slack.IsConfigured)
        {
            notifiers.Add(new MeteredNotifier(new SlackNotifier(_client, options), metrics));
        }

        if (settings.Webhook.IsConfigured)
        {
            notifiers.Add(new MeteredNotifier(new WebhookNotifier(_client, options), metrics));
        }

        Notifiers = notifiers;
        Client = _client;
    }

    public IReadOnlyList<INotifier> Notifiers { get; }

    /// <summary>The client the heartbeat uses too.</summary>
    public HttpClient Client { get; }

    public void Dispose() => _client.Dispose();
}
