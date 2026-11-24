using Fusebox.Application.Budgeting;
using Fusebox.Application.Telemetry;

namespace Fusebox.Infrastructure.Telemetry.Postgres;

/// <summary>The words stored in the database for dispositions and denial reasons. Stable, so queries can rely on them.</summary>
internal static class TelemetryNames
{
    public const string Forwarded = "forwarded";
    public const string Denied = "denied";
    public const string Rejected = "rejected";
    public const string Unavailable = "unavailable";
    public const string Failed = "failed";

    public static string Of(RequestDisposition disposition) => disposition switch
    {
        RequestDisposition.Forwarded => Forwarded,
        RequestDisposition.Denied => Denied,
        RequestDisposition.Rejected => Rejected,
        RequestDisposition.Unavailable => Unavailable,
        RequestDisposition.Failed => Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unknown disposition."),
    };

    public static string Of(DenialReason reason) => reason switch
    {
        DenialReason.BudgetExceeded => "budget_exceeded",
        DenialReason.RequestExceedsBudget => "request_exceeds_budget",
        DenialReason.UnknownModel => "unknown_model",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown denial reason."),
    };
}
