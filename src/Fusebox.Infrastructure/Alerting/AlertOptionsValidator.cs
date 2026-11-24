using Fusebox.Application.Alerting;
using Fusebox.Infrastructure.Telemetry;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Alerting;

/// <summary>Rejects alerting settings that could leave an alert with nowhere to go, and alerting without the database it needs.</summary>
internal sealed class AlertOptionsValidator(IOptions<TelemetryOptions> telemetry) : IValidateOptions<AlertOptions>
{
    private static readonly string[] KnownChannels = [Channels.PagerDuty, Channels.Slack, Channels.Webhook];

    public ValidateOptionsResult Validate(string? name, AlertOptions options)
    {
        var failures = new List<string>();

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (telemetry.Value.Store != TelemetryStoreKind.Postgres)
        {
            failures.Add("Alerts are kept in PostgreSQL, so alerting needs Fusebox:Telemetry:Store to be Postgres.");
        }

        if (!options.PagerDuty.IsConfigured && !options.Slack.IsConfigured && !options.Webhook.IsConfigured)
        {
            failures.Add("Alerting needs at least one channel: a PagerDuty RoutingKey, a Slack WebhookUrl or a Webhook Url.");
        }

        ValidateUrl(options.PagerDuty.IsConfigured, options.PagerDuty.Url, "PagerDuty.Url", failures);
        ValidateUrl(options.Slack.IsConfigured, options.Slack.WebhookUrl, "Slack.WebhookUrl", failures);
        ValidateUrl(options.Webhook.IsConfigured, options.Webhook.Url, "Webhook.Url", failures);
        ValidateUrl(options.Heartbeat.IsConfigured, options.Heartbeat.Url, "Heartbeat.Url", failures);

        foreach (var (severity, channels) in options.Routing)
        {
            if (!Enum.TryParse<AlertSeverity>(severity, ignoreCase: true, out _))
            {
                failures.Add($"Routing has the unknown severity '{severity}'. Use Critical, Warning or Info.");
            }

            foreach (var channel in channels.Where(c => !KnownChannels.Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                failures.Add($"Routing sends {severity} to the unknown channel '{channel}'. Use {string.Join(", ", KnownChannels)}.");
            }
        }

        if (options.SourceInterval < TimeSpan.FromSeconds(1) || options.DispatchInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("SourceInterval and DispatchInterval must be at least one second.");
        }

        if (options.BatchSize < 1 || options.ClaimDuration < options.NotifyTimeout)
        {
            failures.Add("BatchSize must be at least 1, and ClaimDuration at least as long as NotifyTimeout, or a notification could be sent twice at once.");
        }

        if (options.NotifyTimeout < TimeSpan.FromSeconds(1) || options.FirstBackoff < TimeSpan.FromMilliseconds(100) || options.MaxBackoff < options.FirstBackoff)
        {
            failures.Add("NotifyTimeout must be at least a second, and MaxBackoff at least FirstBackoff.");
        }

        if (options.CriticalRatio <= 1 || options.CriticalCooldown < TimeSpan.Zero || options.WarningCooldown < TimeSpan.Zero || options.DiagnosisGrace < TimeSpan.Zero)
        {
            failures.Add("CriticalRatio must be above 1, and the cooldowns and DiagnosisGrace cannot be negative.");
        }

        if (options.BudgetLookback < TimeSpan.FromMinutes(1) || options.AnomalyLookback < TimeSpan.FromMinutes(5))
        {
            failures.Add("BudgetLookback must be at least a minute and AnomalyLookback at least five.");
        }

        if (options.Heartbeat.IsConfigured && options.Heartbeat.Interval < TimeSpan.FromSeconds(10))
        {
            failures.Add("Heartbeat.Interval must be at least ten seconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateUrl(bool configured, string url, string setting, List<string> failures)
    {
        if (configured && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)))
        {
            failures.Add($"{setting} must be an absolute http or https address.");
        }
    }
}
