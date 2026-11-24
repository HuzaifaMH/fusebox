using Fusebox.Domain;

namespace Fusebox.Application.Budgeting;

/// <summary>
/// Port: told when the shared budget store goes away and comes back, so someone can be alerted and
/// the spend made without verification can be reconciled.
/// </summary>
/// <remarks>
/// Methods run on the request path, so implementations must return quickly, for example by queueing the work.
/// An exception thrown here is logged and otherwise ignored.
/// </remarks>
public interface IEnforcementOutageListener
{
    /// <summary>The store has been unreachable long enough that the gateway switched to degraded mode.</summary>
    void OutageStarted(DateTimeOffset at, string reason);

    /// <summary>The outage outlasted the degraded-mode limit, so unverified calls are now rejected.</summary>
    void OutageLimitReached(DateTimeOffset outageStartedAt, DateTimeOffset at);

    /// <summary>The store is reachable again.</summary>
    void OutageEnded(OutageSummary summary);
}

/// <summary>What happened while the store was unreachable.</summary>
/// <param name="StartedAt">When degraded mode began.</param>
/// <param name="EndedAt">When the store came back.</param>
/// <param name="UnverifiedCalls">How many calls were allowed on the local limit alone.</param>
/// <param name="UnverifiedSpend">
/// Each tenant's net spend on those calls. Approximate: calls still in flight when the store returns settle later.
/// </param>
public sealed record OutageSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long UnverifiedCalls,
    IReadOnlyDictionary<TenantId, Money> UnverifiedSpend);
