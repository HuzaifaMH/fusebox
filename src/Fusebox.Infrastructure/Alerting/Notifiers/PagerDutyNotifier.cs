using System.Globalization;
using System.Text;
using System.Text.Json;
using Fusebox.Application.Alerting;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Alerting.Notifiers;

/// <summary>
/// Pages the on-call engineer through the PagerDuty Events API v2. The alert's fingerprint is the dedup key, so a retry or
/// a repeated detection updates the same incident instead of opening another, and a resolve closes it.
/// </summary>
internal sealed class PagerDutyNotifier(HttpClient client, IOptions<AlertOptions> options) : INotifier
{
    // The Events API refuses a summary longer than this.
    private const int MaxSummaryLength = 1_024;

    public string Channel => Channels.PagerDuty;

    public async ValueTask<NotifyOutcome> NotifyAsync(AlertNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var settings = options.Value.PagerDuty;

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url)
        {
            Content = new StringContent(BuildBody(notification, settings.RoutingKey), Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        return NotifierHttp.Interpret(response, "PagerDuty");
    }

    internal static string BuildBody(AlertNotification notification, string routingKey)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("routing_key", routingKey);
            json.WriteString("dedup_key", notification.Fingerprint);

            if (notification.Event == AlertEvent.Resolve)
            {
                json.WriteString("event_action", "resolve");
            }
            else
            {
                // An escalation or an update is a trigger for the same dedup key, which PagerDuty applies to the open incident.
                json.WriteString("event_action", "trigger");
                json.WriteStartObject("payload");
                json.WriteString("summary", Summary(notification));
                json.WriteString("source", "fusebox");
                json.WriteString("severity", Severity(notification.Severity));
                json.WriteString("timestamp", notification.At.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
                json.WriteString("component", notification.Kind.ToString());
                json.WriteString("group", "fusebox");

                json.WriteStartObject("custom_details");
                json.WriteString("summary", notification.Summary);
                foreach (var (key, value) in notification.Details)
                {
                    json.WriteString(key, value);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Summary(AlertNotification notification)
    {
        var text = $"[{notification.Severity.ToString().ToUpperInvariant()}] {notification.Title}: {notification.Summary}";
        return text.Length <= MaxSummaryLength ? text : text[..(MaxSummaryLength - 3)] + "...";
    }

    private static string Severity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "critical",
        AlertSeverity.Warning => "warning",
        _ => "info",
    };
}
