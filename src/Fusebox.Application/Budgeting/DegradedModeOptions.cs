using System.ComponentModel.DataAnnotations;

namespace Fusebox.Application.Budgeting;

/// <summary>How the gateway behaves while the shared budget store is unreachable.</summary>
public sealed class DegradedModeOptions
{
    public const string SectionName = "Fusebox:Enforcement:Degraded";

    /// <summary>
    /// The share of each tenant's budget that one gateway instance may spend on its own, per window, while degraded.
    /// With N instances, up to N times this share can be spent, which is the exposure the outage allows.
    /// </summary>
    [Range(0.01, 1.0)]
    public decimal LocalBudgetFraction { get; set; } = 0.25m;

    /// <summary>How long the gateway keeps serving in degraded mode before it stops serving unverified calls.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the store may take to answer before the call counts as failed. Short, so a slow store adds little latency.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:10")]
    public TimeSpan StoreTimeout { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How many failures in a row make the gateway stop asking the store and degrade.</summary>
    [Range(1, 100)]
    public int FailureThreshold { get; set; } = 3;

    /// <summary>How long to leave the store alone before testing whether it is back.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(5);
}
