namespace Fusebox.Application.Budgeting;

/// <summary>Port: whether budgets are being enforced against the shared store or on this instance's own limit.</summary>
public interface IEnforcementStatus
{
    /// <summary>True while the shared budget store is unreachable and this instance is enforcing a local limit.</summary>
    bool IsDegraded { get; }
}

/// <summary>For stores that have no degraded mode, such as the single-instance in-memory one.</summary>
public sealed class AlwaysHealthyStatus : IEnforcementStatus
{
    public bool IsDegraded => false;
}
