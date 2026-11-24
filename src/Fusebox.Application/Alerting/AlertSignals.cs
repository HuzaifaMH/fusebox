using System.Globalization;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Application.Upstream;
using Fusebox.Domain;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Alerting;

/// <summary>Turns what the system knows into alert signals. Pure, so what an alert says is testable.</summary>
public static class AlertSignals
{
    public const string EnforcementDegradedFingerprint = "enforcement:degraded";
    public const string EnforcementFailClosedFingerprint = "enforcement:fail-closed";

    public static string AnomalyFingerprint(SeriesKey series, Signal signal) =>
        string.Create(CultureInfo.InvariantCulture, $"anomaly:{series.TenantId}:{series.RouteId}:{signal.ToString().ToLowerInvariant()}");

    public static string BudgetFingerprint(TenantId tenant) => $"budget:{tenant}";

    public static string UpstreamFingerprint(string cluster) => $"upstream:{cluster}";

    /// <summary>
    /// The alert for an open anomaly, or null if it is too soon to raise it. The first ones wait a short while for the
    /// diagnosis so they can carry it. After that they go out without it, and the diagnosis follows as an update.
    /// </summary>
    public static AlertSignal? ForAnomaly(AnomalyAlertState state, AlertOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        if (state.Diagnosis is null && now - state.OpenedAt < options.DiagnosisGrace)
        {
            return null;
        }

        var name = AnomalyDescription.Name(state.Signal);
        var culture = CultureInfo.InvariantCulture;
        var baselineWord = state.Basis == BaselineBasis.Fixed ? "Fixed limit" : "Baseline";

        var summary = state.Diagnosis?.Diagnosis
            ?? string.Create(
                culture,
                $"{name} reached {AnomalyDescription.Format(state.Signal, state.OpenedValue)}, against {baselineWord.ToLowerInvariant()} of {AnomalyDescription.Format(state.Signal, state.Baseline)}. A diagnosis is on its way.");

        var details = new Dictionary<string, string>
        {
            ["Tenant"] = state.Series.TenantId.Value,
            ["Route"] = state.Series.RouteId,
            ["Signal"] = name,
            ["Started"] = state.OpenedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", culture),
            ["Peak"] = string.Create(culture, $"{AnomalyDescription.Format(state.Signal, state.PeakValue)} ({state.PeakRatio:0.#}x)"),
            [baselineWord] = AnomalyDescription.Format(state.Signal, state.Baseline),
        };

        if (state.Diagnosis is { } diagnosis)
        {
            details["Suggested next step"] = diagnosis.Remediation;
            details["Explanation"] = diagnosis.Source == NarrationSource.Llm
                ? $"AI-suggested, checked against the numbers. Confidence {diagnosis.Confidence.ToString().ToLowerInvariant()}."
                : $"Worked out from the numbers. Confidence {diagnosis.Confidence.ToString().ToLowerInvariant()}.";
        }

        return new AlertSignal(
            AnomalyFingerprint(state.Series, state.Signal),
            AlertKind.Anomaly,
            state.PeakRatio >= options.CriticalRatio ? AlertSeverity.Critical : AlertSeverity.Warning,
            $"{name} anomaly: {state.Series}",
            summary,
            details,
            state.Diagnosis is null ? "detected" : $"diagnosed-{state.Diagnosis.Source.ToString().ToLowerInvariant()}",
            now);
    }

    public static string AnomalyEndedNote(AnomalyAlertState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var lasted = (state.ClosedAt ?? state.OpenedAt) - state.OpenedAt;
        return $"The anomaly ended after {Duration(lasted)}. It peaked at {AnomalyDescription.Format(state.Signal, state.PeakValue)}.";
    }

    public static AlertSignal ForBudget(BudgetBlock block, AlertOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(options);

        var minutes = (int)Math.Round(options.BudgetLookback.TotalMinutes);

        return new AlertSignal(
            BudgetFingerprint(block.TenantId),
            AlertKind.BudgetExhausted,
            options.BudgetSeverity,
            $"Budget exhausted: {block.TenantId}",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{block.BlockedCalls:N0} calls from {block.TenantId} were blocked in the last {minutes} minutes because its budget is used up. Its calls fail until the window resets or the budget is raised."),
            new Dictionary<string, string>
            {
                ["Tenant"] = block.TenantId.Value,
                ["Calls blocked"] = block.BlockedCalls.ToString("N0", CultureInfo.InvariantCulture),
                ["Last blocked"] = block.LastBlockedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            },
            "blocked",
            now);
    }

    public static AlertSignal ForDegradedEnforcement(DateTimeOffset at, string reason) => new(
        EnforcementDegradedFingerprint,
        AlertKind.EnforcementDegraded,
        AlertSeverity.Critical,
        "Budget store unreachable: serving in degraded mode",
        "The gateway cannot reach the shared budget store. It is still serving, but only on a strict local limit per instance, so budgets are no longer verified. It stops serving unverified calls if this lasts.",
        new Dictionary<string, string>
        {
            ["Since"] = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            ["Reason"] = reason,
        },
        "degraded",
        at);

    public static AlertSignal ForFailClosedEnforcement(DateTimeOffset outageStartedAt, DateTimeOffset at) => new(
        EnforcementFailClosedFingerprint,
        AlertKind.EnforcementFailClosed,
        AlertSeverity.Critical,
        "Budget store down too long: rejecting unverified calls",
        string.Create(
            CultureInfo.InvariantCulture,
            $"The budget store has been unreachable for {Duration(at - outageStartedAt)}, longer than degraded mode allows. Calls that cannot be verified are now being rejected with 503."),
        new Dictionary<string, string>
        {
            ["Since"] = outageStartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
        },
        "fail-closed",
        at);

    public static AlertSignal ForUpstreamUnavailable(string cluster, DateTimeOffset at, string lastFailure, TimeSpan retryEvery) => new(
        UpstreamFingerprint(cluster),
        AlertKind.UpstreamUnavailable,
        AlertSeverity.Critical,
        $"Upstream API failing: {cluster}",
        string.Create(
            CultureInfo.InvariantCulture,
            $"Calls to {cluster} have failed repeatedly, so Fusebox has cut it off. Callers get an immediate 503 instead of waiting for a timeout, and no budget is held for those calls. One test call goes through every {Duration(retryEvery)} to see whether it is back."),
        new Dictionary<string, string>
        {
            ["Upstream"] = cluster,
            ["Since"] = at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            ["Last failure"] = lastFailure,
            ["Suggested next step"] = "Check the vendor's status page. This clears by itself when the vendor answers a test call.",
        },
        "unavailable",
        at);

    public static string UpstreamRecoveredNote(DateTimeOffset openedAt, DateTimeOffset at) =>
        $"The upstream answered a test call after being cut off for {Duration(at - openedAt)}. Calls to it flow again.";

    /// <summary>What to tell people when the budget store is back, including what was spent without verification.</summary>
    public static string OutageEndedNote(OutageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var culture = CultureInfo.InvariantCulture;
        var total = summary.UnverifiedSpend.Values.Aggregate(Money.Zero, (sum, spend) => sum + spend);
        var top = string.Join(
            ", ",
            summary.UnverifiedSpend
                .OrderByDescending(pair => pair.Value)
                .Take(3)
                .Select(pair => string.Create(culture, $"{pair.Key} {pair.Value}")));

        var calls = summary.UnverifiedCalls.ToString("N0", culture);
        var lasted = Duration(summary.EndedAt - summary.StartedAt);
        var largest = top.Length > 0 ? $" (largest: {top})." : ".";

        return $"The budget store was unreachable for {lasted}. {calls} calls were allowed without verification, about {total} in total{largest}";
    }

    public static string Duration(TimeSpan span)
    {
        var culture = CultureInfo.InvariantCulture;

        return span.TotalSeconds switch
        {
            < 90 => Math.Max(span.TotalSeconds, 1).ToString("0", culture) + " seconds",
            < 5400 => span.TotalMinutes.ToString("0", culture) + " minutes",
            _ => span.TotalHours.ToString("0.#", culture) + " hours",
        };
    }
}

/// <summary>Something an event-driven source wants done, queued so that raising it never holds up what noticed it.</summary>
public abstract record QueuedAlert
{
    private protected QueuedAlert()
    {
    }
}

public sealed record QueuedRaise(AlertSignal Signal) : QueuedAlert;

public sealed record QueuedResolve(string Fingerprint, DateTimeOffset At, string? Note) : QueuedAlert;

/// <summary>Port: where event-driven sources, which must not wait, leave alerts to be raised.</summary>
public interface IAlertInbox
{
    /// <summary>Takes the item and returns at once. Returns false if it could not be taken.</summary>
    bool TryPost(QueuedAlert alert);
}

/// <summary>
/// Raises an alert when the budget store goes away, escalates it if the outage outlasts degraded mode, and resolves it
/// with a note on what was spent unverified when the store is back.
/// </summary>
public sealed class AlertingOutageListener(IAlertInbox inbox) : IEnforcementOutageListener
{
    public void OutageStarted(DateTimeOffset at, string reason) =>
        inbox.TryPost(new QueuedRaise(AlertSignals.ForDegradedEnforcement(at, reason)));

    public void OutageLimitReached(DateTimeOffset outageStartedAt, DateTimeOffset at) =>
        inbox.TryPost(new QueuedRaise(AlertSignals.ForFailClosedEnforcement(outageStartedAt, at)));

    public void OutageEnded(OutageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var note = AlertSignals.OutageEndedNote(summary);

        inbox.TryPost(new QueuedResolve(AlertSignals.EnforcementFailClosedFingerprint, summary.EndedAt, note));
        inbox.TryPost(new QueuedResolve(AlertSignals.EnforcementDegradedFingerprint, summary.EndedAt, note));
    }
}

/// <summary>Raises an alert when a vendor is cut off for failing, and resolves it when the vendor answers again.</summary>
public sealed class AlertingUpstreamListener(IAlertInbox inbox, IOptions<UpstreamBreakerOptions> options) : IUpstreamHealthListener
{
    public void CircuitOpened(string cluster, DateTimeOffset at, string lastFailure) =>
        inbox.TryPost(new QueuedRaise(AlertSignals.ForUpstreamUnavailable(cluster, at, lastFailure, options.Value.OpenFor)));

    public void CircuitClosed(string cluster, DateTimeOffset openedAt, DateTimeOffset at) =>
        inbox.TryPost(new QueuedResolve(AlertSignals.UpstreamFingerprint(cluster), at, AlertSignals.UpstreamRecoveredNote(openedAt, at)));
}
