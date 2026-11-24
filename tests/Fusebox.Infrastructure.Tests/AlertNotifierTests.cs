using System.Net;
using System.Text.Json;
using Fusebox.Application.Alerting;
using Fusebox.Infrastructure.Alerting;
using Fusebox.Infrastructure.Alerting.Notifiers;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Tests;

internal sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        return respond(request, body);
    }
}

internal static class NotifierSamples
{
    public static AlertNotification Notification(
        AlertEvent kind = AlertEvent.Trigger,
        AlertSeverity severity = AlertSeverity.Critical,
        string title = "Spend anomaly: acme on openai-chat",
        string summary = "Spend jumped to 12x its usual level.",
        string? note = null) =>
        new(
            kind,
            Guid.NewGuid(),
            "anomaly:acme:openai-chat:spend",
            AlertKind.Anomaly,
            severity,
            title,
            summary,
            new Dictionary<string, string> { ["Tenant"] = "acme", ["Suggested next step"] = "Look for a retry loop." },
            DateTimeOffset.Parse("2026-03-04T12:00:00Z"),
            DateTimeOffset.Parse("2026-03-04T12:05:00Z"),
            note);

    public static IOptions<AlertOptions> Options(Action<AlertOptions>? configure = null)
    {
        var options = new AlertOptions
        {
            Enabled = true,
            PagerDuty = new PagerDutyOptions { RoutingKey = "rk-123", Url = "https://pd.test/v2/enqueue" },
            Slack = new SlackOptions { WebhookUrl = "https://slack.test/hook" },
            Webhook = new WebhookOptions { Url = "https://hook.test/in", BearerToken = "secret-token" },
        };
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    public static HttpResponseMessage Reply(HttpStatusCode status) => new(status);
}

public class AlertNotifierTests
{
    [Fact]
    public async Task PagerDuty_triggers_with_the_fingerprint_as_dedup_key()
    {
        var handler = new StubHandler((_, _) => NotifierSamples.Reply(HttpStatusCode.Accepted));
        using var client = new HttpClient(handler);
        var notifier = new PagerDutyNotifier(client, NotifierSamples.Options());

        var outcome = await notifier.NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);

        Assert.IsType<Delivered>(outcome);
        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://pd.test/v2/enqueue", request.RequestUri!.ToString());

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("rk-123", root.GetProperty("routing_key").GetString());
        Assert.Equal("anomaly:acme:openai-chat:spend", root.GetProperty("dedup_key").GetString());
        Assert.Equal("trigger", root.GetProperty("event_action").GetString());
        Assert.Equal("critical", root.GetProperty("payload").GetProperty("severity").GetString());
        Assert.Contains("Spend anomaly", root.GetProperty("payload").GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("acme", root.GetProperty("payload").GetProperty("custom_details").GetProperty("Tenant").GetString());
    }

    [Fact]
    public void PagerDuty_resolve_closes_the_incident_without_a_payload()
    {
        var body = PagerDutyNotifier.BuildBody(NotifierSamples.Notification(AlertEvent.Resolve), "rk-123");

        using var json = JsonDocument.Parse(body);
        Assert.Equal("resolve", json.RootElement.GetProperty("event_action").GetString());
        Assert.Equal("anomaly:acme:openai-chat:spend", json.RootElement.GetProperty("dedup_key").GetString());
        Assert.False(json.RootElement.TryGetProperty("payload", out _));
    }

    [Fact]
    public void PagerDuty_keeps_the_summary_within_the_limit_it_accepts()
    {
        var body = PagerDutyNotifier.BuildBody(NotifierSamples.Notification(summary: new string('x', 5_000)), "rk");

        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("payload").GetProperty("summary").GetString()!.Length <= 1_024);
    }

    [Theory]
    [InlineData(AlertSeverity.Critical, "critical")]
    [InlineData(AlertSeverity.Warning, "warning")]
    [InlineData(AlertSeverity.Info, "info")]
    public void PagerDuty_maps_severities(AlertSeverity severity, string expected)
    {
        var body = PagerDutyNotifier.BuildBody(NotifierSamples.Notification(severity: severity), "rk");

        using var json = JsonDocument.Parse(body);
        Assert.Equal(expected, json.RootElement.GetProperty("payload").GetProperty("severity").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    public async Task A_success_is_delivered(HttpStatusCode status)
    {
        using var client = new HttpClient(new StubHandler((_, _) => NotifierSamples.Reply(status)));

        var outcome = await new WebhookNotifier(client, NotifierSamples.Options()).NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);

        Assert.IsType<Delivered>(outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_busy_or_broken_channel_is_retried_later(HttpStatusCode status)
    {
        using var client = new HttpClient(new StubHandler((_, _) => NotifierSamples.Reply(status)));

        var outcome = await new SlackNotifier(client, NotifierSamples.Options()).NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);

        Assert.IsType<RetryLater>(outcome);
    }

    [Fact]
    public async Task A_retry_after_header_is_passed_on()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = NotifierSamples.Reply(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        }));

        var outcome = await new PagerDutyNotifier(client, NotifierSamples.Options()).NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);

        var retry = Assert.IsType<RetryLater>(outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), retry.After);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refusal_that_will_not_change_is_rejected_without_echoing_the_answer(HttpStatusCode status)
    {
        using var client = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(status) { Content = new StringContent("secret rk-123 leaked") }));

        var outcome = await new PagerDutyNotifier(client, NotifierSamples.Options()).NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);

        var rejected = Assert.IsType<Rejected>(outcome);
        Assert.DoesNotContain("rk-123", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_sends_the_event_and_the_bearer_token()
    {
        var handler = new StubHandler((_, _) => NotifierSamples.Reply(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        await new WebhookNotifier(client, NotifierSamples.Options()).NotifyAsync(NotifierSamples.Notification(AlertEvent.Escalate), CancellationToken.None);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", request.Headers.Authorization.Parameter);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("escalate", json.RootElement.GetProperty("event").GetString(), ignoreCase: true);
    }

    [Fact]
    public async Task Webhook_without_a_token_sends_no_authorization()
    {
        var handler = new StubHandler((_, _) => NotifierSamples.Reply(HttpStatusCode.OK));
        using var client = new HttpClient(handler);

        await new WebhookNotifier(client, NotifierSamples.Options(o => o.Webhook.BearerToken = string.Empty))
            .NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);

        Assert.Null(Assert.Single(handler.Calls).Request.Headers.Authorization);
    }

    [Fact]
    public void Slack_escapes_what_chat_would_treat_as_markup()
    {
        // JSON writes < and & as escape sequences; read them back as the text Slack will receive.
        var body = System.Text.RegularExpressions.Regex.Unescape(SlackNotifier.BuildBody(NotifierSamples.Notification(summary: "<!channel> a & b")));

        Assert.DoesNotContain("<!channel>", body, StringComparison.Ordinal);
        Assert.Contains("&lt;!channel&gt;", body, StringComparison.Ordinal);
        Assert.Contains("&amp;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Slack_says_in_words_that_an_alert_is_resolved()
    {
        var body = SlackNotifier.BuildBody(NotifierSamples.Notification(AlertEvent.Resolve, note: "It lasted 12 minutes."));

        Assert.Contains("esolved", body, StringComparison.Ordinal);
        Assert.Contains("12 minutes", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_catalog_builds_only_the_channels_that_are_configured()
    {
        using var factory = new TestMeterFactory();
        using var metrics = new AlertMetrics(factory);
        var options = NotifierSamples.Options(o =>
        {
            o.Slack.WebhookUrl = string.Empty;
            o.Webhook.Url = string.Empty;
        });

        using var catalog = new NotifierCatalog(options, metrics);
        await Task.CompletedTask;

        Assert.Equal([Channels.PagerDuty], catalog.Notifiers.Select(n => n.Channel));
    }

    [Fact]
    public async Task Delivery_results_are_counted_by_channel()
    {
        using var factory = new TestMeterFactory();
        using var metrics = new AlertMetrics(factory);
        var seen = new List<(string Channel, string Result)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AlertMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? channel = null, result = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "channel")
                {
                    channel = (string?)tag.Value;
                }
                else if (tag.Key == "result")
                {
                    result = (string?)tag.Value;
                }
            }

            seen.Add((channel!, result!));
        });
        listener.Start();

        using var ok = new HttpClient(new StubHandler((_, _) => NotifierSamples.Reply(HttpStatusCode.OK)));
        using var bad = new HttpClient(new StubHandler((_, _) => NotifierSamples.Reply(HttpStatusCode.Unauthorized)));

        await new MeteredNotifier(new SlackNotifier(ok, NotifierSamples.Options()), metrics).NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);
        await new MeteredNotifier(new PagerDutyNotifier(bad, NotifierSamples.Options()), metrics).NotifyAsync(NotifierSamples.Notification(), CancellationToken.None);

        Assert.Contains((Channels.Slack, "delivered"), seen);
        Assert.Contains((Channels.PagerDuty, "rejected"), seen);
    }
}
