using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fusebox.Application.Alerting;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Alerting.Notifiers;

/// <summary>
/// Posts alerts as JSON to any address, so a team can bridge them to Teams, email, Opsgenie or whatever it already uses.
/// </summary>
internal sealed class WebhookNotifier(HttpClient client, IOptions<AlertOptions> options) : INotifier
{
    public string Channel => Channels.Webhook;

    public async ValueTask<NotifyOutcome> NotifyAsync(AlertNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var settings = options.Value.Webhook;

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url)
        {
            Content = new StringContent(BuildBody(notification), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(settings.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.BearerToken);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        return NotifierHttp.Interpret(response, "The webhook");
    }

    internal static string BuildBody(AlertNotification notification)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("event", notification.Event.ToString().ToLowerInvariant());
            json.WriteString("at", notification.At.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

            json.WriteStartObject("alert");
            json.WriteString("id", notification.AlertId);
            json.WriteString("fingerprint", notification.Fingerprint);
            json.WriteString("kind", notification.Kind.ToString().ToLowerInvariant());
            json.WriteString("severity", notification.Severity.ToString().ToLowerInvariant());
            json.WriteString("title", notification.Title);
            json.WriteString("summary", notification.Summary);
            json.WriteString("openedAt", notification.OpenedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));

            json.WriteStartObject("details");
            foreach (var (key, value) in notification.Details)
            {
                json.WriteString(key, value);
            }

            json.WriteEndObject();
            json.WriteEndObject();

            if (notification.Note is { Length: > 0 } note)
            {
                json.WriteString("note", note);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
