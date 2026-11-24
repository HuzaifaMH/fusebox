using Fusebox.Domain;

namespace Fusebox.Application.Detection;

/// <summary>A measurement taken every minute for each tenant and route, which the detector watches for sudden jumps.</summary>
public enum Signal
{
    /// <summary>Requests per minute.</summary>
    Volume,

    /// <summary>Money charged per minute, in micro-dollars.</summary>
    Spend,

    /// <summary>The share of forwarded calls the vendor answered with an error, from 0 to 1.</summary>
    ErrorRate,

    /// <summary>The 95th percentile of how long forwarded calls took, in milliseconds.</summary>
    LatencyP95,

    /// <summary>Average input tokens per forwarded call.</summary>
    InputTokensPerRequest,

    /// <summary>Average output tokens per forwarded call.</summary>
    OutputTokensPerRequest,

    /// <summary>The share of forwarded calls that repeat one sent a moment before, from 0 to 1. A retry loop sends the same call again and again.</summary>
    RepeatRate,
}

/// <summary>One stream of measurements: a tenant's calls on one route.</summary>
public readonly record struct SeriesKey(TenantId TenantId, string RouteId)
{
    public override string ToString() => $"{TenantId} on {RouteId}";
}
