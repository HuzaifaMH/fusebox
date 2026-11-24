using System.Text.Json;
using System.Text.Json.Serialization;
using Fusebox.Application.Alerting;

namespace Fusebox.Infrastructure.Alerting.Postgres;

/// <summary>The words and JSON stored in the database for alerts. Stable, so queries can rely on them.</summary>
internal static class AlertNames
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static string Of(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Info => "info",
        AlertSeverity.Warning => "warning",
        AlertSeverity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity."),
    };

    public static AlertSeverity SeverityOf(string name) => name switch
    {
        "info" => AlertSeverity.Info,
        "warning" => AlertSeverity.Warning,
        "critical" => AlertSeverity.Critical,
        _ => throw new FormatException($"Unknown severity '{name}'."),
    };

    public static string Of(AlertKind kind) => kind switch
    {
        AlertKind.Anomaly => "anomaly",
        AlertKind.BudgetExhausted => "budget_exhausted",
        AlertKind.EnforcementDegraded => "enforcement_degraded",
        AlertKind.EnforcementFailClosed => "enforcement_fail_closed",
        AlertKind.UpstreamUnavailable => "upstream_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind."),
    };

    public static AlertKind KindOf(string name) => name switch
    {
        "anomaly" => AlertKind.Anomaly,
        "budget_exhausted" => AlertKind.BudgetExhausted,
        "enforcement_degraded" => AlertKind.EnforcementDegraded,
        "enforcement_fail_closed" => AlertKind.EnforcementFailClosed,
        "upstream_unavailable" => AlertKind.UpstreamUnavailable,
        _ => throw new FormatException($"Unknown alert kind '{name}'."),
    };

    public static string Of(AlertEvent alertEvent) => alertEvent switch
    {
        AlertEvent.Trigger => "trigger",
        AlertEvent.Escalate => "escalate",
        AlertEvent.Update => "update",
        AlertEvent.Resolve => "resolve",
        _ => throw new ArgumentOutOfRangeException(nameof(alertEvent), alertEvent, "Unknown event."),
    };

    public static string Serialize(AlertNotification notification) => JsonSerializer.Serialize(notification, Json);

    public static AlertNotification DeserializeNotification(string json) =>
        JsonSerializer.Deserialize<AlertNotification>(json, Json) ?? throw new FormatException("The saved notification is empty.");

    public static string Serialize(IReadOnlyDictionary<string, string> details) => JsonSerializer.Serialize(details, Json);

    public static IReadOnlyDictionary<string, string> DeserializeDetails(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [];
}
