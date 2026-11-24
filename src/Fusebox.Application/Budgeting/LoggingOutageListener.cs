using Fusebox.Domain;
using Microsoft.Extensions.Logging;

namespace Fusebox.Application.Budgeting;

/// <summary>Writes outage events to the log. The default listener, always on.</summary>
internal sealed partial class LoggingOutageListener(ILogger<LoggingOutageListener> logger) : IEnforcementOutageListener
{
    private readonly ILogger<LoggingOutageListener> _logger = logger;

    public void OutageStarted(DateTimeOffset at, string reason) => LogStarted(at, reason);

    public void OutageLimitReached(DateTimeOffset outageStartedAt, DateTimeOffset at) =>
        LogLimitReached(outageStartedAt, at - outageStartedAt);

    public void OutageEnded(OutageSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        LogEnded(summary.StartedAt, summary.EndedAt - summary.StartedAt, summary.UnverifiedCalls);

        foreach (var (tenantId, spend) in summary.UnverifiedSpend)
        {
            LogUnverifiedSpend(tenantId, spend.MicroDollars);
        }
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Critical,
        Message = "The budget store is unreachable since {At}; serving in degraded mode on a local limit. Reason: {Reason}")]
    private partial void LogStarted(DateTimeOffset at, string reason);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Critical,
        Message = "The budget store has been unreachable since {StartedAt} ({Duration}), the degraded-mode limit; rejecting calls that cannot be verified")]
    private partial void LogLimitReached(DateTimeOffset startedAt, TimeSpan duration);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
        Message = "The budget store is back. It was unreachable since {StartedAt} ({Duration}); {UnverifiedCalls} calls were allowed without verification")]
    private partial void LogEnded(DateTimeOffset startedAt, TimeSpan duration, long unverifiedCalls);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning,
        Message = "Unverified spend during the outage for tenant {TenantId}: {UnverifiedMicroDollars} micro-dollars")]
    private partial void LogUnverifiedSpend(TenantId tenantId, long unverifiedMicroDollars);
}
