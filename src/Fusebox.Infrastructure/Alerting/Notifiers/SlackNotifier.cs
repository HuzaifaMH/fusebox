using System.Text;
using System.Text.Json;
using Fusebox.Application.Alerting;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Alerting.Notifiers;

/// <summary>Posts alerts to a Slack channel through an incoming webhook.</summary>
internal sealed class SlackNotifier(HttpClient client, IOptions<AlertOptions> options) : INotifier
{
    // Slack refuses a header longer than 150 characters, a section longer than 3,000, and more than 10 fields.
    private const int MaxHeader = 150;
    private const int MaxSection = 2_800;
    private const int MaxFieldValue = 300;
    private const int MaxFields = 10;

    public string Channel => Channels.Slack;

    public async ValueTask<NotifyOutcome> NotifyAsync(AlertNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Slack.WebhookUrl)
        {
            Content = new StringContent(BuildBody(notification), Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        return NotifierHttp.Interpret(response, "Slack");
    }

    internal static string BuildBody(AlertNotification notification)
    {
        var headline = Truncate($"{Emoji(notification)} {Lead(notification)}: {notification.Title}", MaxHeader);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();

            // Shown in notifications and wherever blocks cannot be. Slack reads markup here too, so it is escaped like the rest.
            json.WriteString("text", Escape(Truncate($"{Lead(notification)}: {notification.Title}. {notification.Summary}", 1_000)));

            json.WriteStartArray("blocks");
            WriteBlock(json, "header", text => text.WriteString("type", "plain_text"), headline);
            WriteBlock(json, "section", text => text.WriteString("type", "mrkdwn"), Escape(Truncate(notification.Summary, MaxSection)));

            if (notification.Note is { Length: > 0 } note)
            {
                WriteBlock(json, "section", text => text.WriteString("type", "mrkdwn"), Escape(Truncate(note, MaxSection)));
            }

            if (notification.Details.Count > 0)
            {
                json.WriteStartObject();
                json.WriteString("type", "section");
                json.WriteStartArray("fields");

                foreach (var (key, value) in notification.Details.Take(MaxFields))
                {
                    json.WriteStartObject();
                    json.WriteString("type", "mrkdwn");
                    json.WriteString("text", $"*{Escape(key)}*\n{Escape(Truncate(value, MaxFieldValue))}");
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteStartObject();
            json.WriteString("type", "context");
            json.WriteStartArray("elements");
            json.WriteStartObject();
            json.WriteString("type", "mrkdwn");
            json.WriteString("text", $"Fusebox  |  {notification.Severity.ToString().ToLowerInvariant()}  |  `{Escape(notification.Fingerprint)}`");
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // What the message is about, in a word.
    private static string Lead(AlertNotification notification) => notification.Event switch
    {
        AlertEvent.Resolve => "Resolved",
        AlertEvent.Escalate => $"Escalated to {notification.Severity.ToString().ToUpperInvariant()}",
        AlertEvent.Update => "Update",
        _ => notification.Severity.ToString().ToUpperInvariant(),
    };

    private static string Emoji(AlertNotification notification) => notification.Event switch
    {
        AlertEvent.Resolve => ":white_check_mark:",
        AlertEvent.Escalate => ":arrow_up:",
        AlertEvent.Update => ":mag:",
        _ => notification.Severity switch
        {
            AlertSeverity.Critical => ":rotating_light:",
            AlertSeverity.Warning => ":warning:",
            _ => ":information_source:",
        },
    };

    private static void WriteBlock(Utf8JsonWriter json, string type, Action<Utf8JsonWriter> writeKind, string text)
    {
        json.WriteStartObject();
        json.WriteString("type", type);
        json.WriteStartObject("text");
        writeKind(json);
        json.WriteString("text", text);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    // Slack reads &, < and > in a message as markup, so text that came from data cannot be allowed to make links or mentions.
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
