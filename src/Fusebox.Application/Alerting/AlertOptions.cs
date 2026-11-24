namespace Fusebox.Application.Alerting;

/// <summary>How alerts are raised, routed, retried and kept from becoming noise.</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Fusebox:Alerting";

    /// <summary>Turns alerting on. Off by default, because it needs a database and at least one channel.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which channels each severity goes to, by name. By default critical alerts page and the rest go to chat.</summary>
    public Dictionary<string, List<string>> Routing { get; set; } = new()
    {
        [nameof(AlertSeverity.Critical)] = [Channels.PagerDuty],
        [nameof(AlertSeverity.Warning)] = [Channels.Slack],
        [nameof(AlertSeverity.Info)] = [Channels.Slack],
    };

    /// <summary>How often the sources of alerts are asked what is wrong, such as the anomalies that are open.</summary>
    public TimeSpan SourceInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often saved notifications that are due are sent.</summary>
    public TimeSpan DispatchInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most notifications taken for sending at once.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>How long a notification taken for sending is held, so a gateway instance that dies mid-send does not strand it.</summary>
    public TimeSpan ClaimDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long a channel has to accept a notification before it counts as failed.</summary>
    public TimeSpan NotifyTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The pause after a first failed attempt. It doubles with each further failure.</summary>
    public TimeSpan FirstBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The longest pause between attempts. A notification is retried at this pace until the channel accepts it.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>After a critical alert resolves, how long before the same incident may raise a new one.</summary>
    public TimeSpan CriticalCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>After a warning or info alert resolves, how long before the same incident may raise a new one.</summary>
    public TimeSpan WarningCooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>An anomaly alert starts as a warning and becomes critical when the signal reaches this many times its baseline.</summary>
    public double CriticalRatio { get; set; } = 10;

    /// <summary>
    /// How long an anomaly alert waits for its diagnosis before going out without it. The diagnosis is sent as an update
    /// when it arrives. Alerts never wait on it for longer than this.
    /// </summary>
    public TimeSpan DiagnosisGrace { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Alert when a tenant's calls are being blocked because its budget is used up.</summary>
    public bool BudgetAlerts { get; set; } = true;

    public AlertSeverity BudgetSeverity { get; set; } = AlertSeverity.Critical;

    /// <summary>A tenant is alerted about if any of its calls were blocked for budget within this long, and the alert resolves once none are.</summary>
    public TimeSpan BudgetLookback { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How far back anomalies are looked at for alerting. Longer than any anomaly is expected to take to be noticed.</summary>
    public TimeSpan AnomalyLookback { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The Events API of PagerDuty, which pages the on-call engineer.</summary>
    public PagerDutyOptions PagerDuty { get; set; } = new();

    public SlackOptions Slack { get; set; } = new();

    /// <summary>A generic JSON webhook, to bridge to Teams, email, Opsgenie or anything else.</summary>
    public WebhookOptions Webhook { get; set; } = new();

    /// <summary>An external monitor that raises the alarm if Fusebox stops pinging it. Fusebox cannot report its own death.</summary>
    public HeartbeatOptions Heartbeat { get; set; } = new();
}

public static class Channels
{
    public const string PagerDuty = "pagerduty";
    public const string Slack = "slack";
    public const string Webhook = "webhook";
}

public sealed class PagerDutyOptions
{
    public string Url { get; set; } = "https://events.pagerduty.com/v2/enqueue";

    /// <summary>The integration key of a PagerDuty service. Supply it through an environment variable.</summary>
    public string RoutingKey { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(RoutingKey);
}

public sealed class SlackOptions
{
    /// <summary>An incoming webhook address. It is a secret: supply it through an environment variable.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(WebhookUrl);
}

public sealed class WebhookOptions
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Sent as a bearer token, if set.</summary>
    public string BearerToken { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}

public sealed class HeartbeatOptions
{
    /// <summary>The address to ping, for example a Healthchecks.io or Better Stack heartbeat URL. Empty means no heartbeat.</summary>
    public string Url { get; set; } = string.Empty;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);
}
