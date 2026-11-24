using Fusebox.Application.Budgeting;
using Fusebox.Domain;

namespace Fusebox.Application.Telemetry;

/// <summary>How the gateway dealt with a request.</summary>
public enum RequestDisposition
{
    /// <summary>The call went to the vendor. <see cref="RequestRecord.StatusCode"/> says how the vendor answered.</summary>
    Forwarded,

    /// <summary>Fusebox refused the call because of the tenant's budget or a missing price.</summary>
    Denied,

    /// <summary>Fusebox refused the call because the request itself was unusable, for example an invalid or oversized body.</summary>
    Rejected,

    /// <summary>Fusebox could not verify the budget and was not allowed to proceed without it.</summary>
    Unavailable,

    /// <summary>An unexpected error inside the gateway.</summary>
    Failed,
}

/// <summary>
/// What happened to one request, kept for dashboards, anomaly detection and diagnosis.
/// Only metadata: prompts and responses are never recorded.
/// </summary>
/// <param name="Id">A unique id, ordered by time.</param>
/// <param name="StartedAt">When the gateway received the request.</param>
/// <param name="TenantId">Who made the call.</param>
/// <param name="RouteId">Which route it came in on, for example "openai-chat".</param>
/// <param name="Model">The model asked for, or null when the route has no model or the body was unusable.</param>
/// <param name="Disposition">How the gateway dealt with it.</param>
/// <param name="DenialReason">Why it was denied, when <paramref name="Disposition"/> is <see cref="RequestDisposition.Denied"/>.</param>
/// <param name="StatusCode">The HTTP status the caller received.</param>
/// <param name="Duration">Time from receiving the request to finishing the response. For a streamed response, the whole stream.</param>
/// <param name="IsStreaming">Whether the response was streamed.</param>
/// <param name="Usage">Tokens the vendor reported, or null if none are known.</param>
/// <param name="Cost">What the call was charged against the budget.</param>
/// <param name="Reserved">What was held before the call, the worst case.</param>
/// <param name="IsRepeat">Whether it repeats a request sent a moment before (see <see cref="RepeatDetector"/>), the signature of a retry loop.</param>
public sealed record RequestRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    TenantId TenantId,
    string RouteId,
    string? Model,
    RequestDisposition Disposition,
    DenialReason? DenialReason,
    int StatusCode,
    TimeSpan Duration,
    bool IsStreaming,
    TokenUsage? Usage,
    Money Cost,
    Money Reserved,
    bool IsRepeat = false);
