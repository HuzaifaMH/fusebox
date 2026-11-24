using Fusebox.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Alerting;

/// <summary>
/// One round of looking for things to alert about: anomalies that are open or have ended, and tenants whose calls are being
/// blocked for budget. Raising is idempotent, so a round that repeats earlier work does no harm.
/// </summary>
public sealed partial class AlertSourceRunner
{
    private readonly IAlertFeed _feed;
    private readonly IAlertStore _store;
    private readonly AlertService _service;
    private readonly AlertOptions _options;
    private readonly ILogger<AlertSourceRunner> _logger;

    public AlertSourceRunner(
        IAlertFeed feed,
        IAlertStore store,
        AlertService service,
        IOptions<AlertOptions> options,
        ILogger<AlertSourceRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _feed = feed;
        _store = store;
        _service = service;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Runs one round as of <paramref name="now"/>. Does nothing if another gateway instance is already running one.</summary>
    public async ValueTask RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await _feed.TryRunExclusivelyAsync(token => EvaluateAsync(now, token), cancellationToken))
        {
            LogSkipped();
        }
    }

    private async Task EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EvaluateAnomaliesAsync(now, cancellationToken);

        if (_options.BudgetAlerts)
        {
            await EvaluateBudgetsAsync(now, cancellationToken);
        }
    }

#pragma warning disable CA1031 // One alert that cannot be raised must not stop the others. The next round tries it again.
    private async Task EvaluateAnomaliesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var anomalies = await _feed.LoadAnomaliesAsync(now - _options.AnomalyLookback, cancellationToken);

        foreach (var anomaly in anomalies)
        {
            try
            {
                if (anomaly.IsOpen)
                {
                    if (AlertSignals.ForAnomaly(anomaly, _options, now) is { } signal)
                    {
                        await _service.RaiseAsync(signal, cancellationToken);
                    }
                }
                else if (anomaly.ClosedAt is { } closedAt)
                {
                    await _service.ResolveAsync(
                        AlertSignals.AnomalyFingerprint(anomaly.Series, anomaly.Signal),
                        closedAt,
                        AlertSignals.AnomalyEndedNote(anomaly),
                        cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(exception, anomaly.Id);
            }
        }
    }

    private async Task EvaluateBudgetsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var blocks = await _feed.LoadBudgetBlocksAsync(now - _options.BudgetLookback, cancellationToken);
        var blocked = blocks.Select(block => AlertSignals.BudgetFingerprint(block.TenantId)).ToHashSet(StringComparer.Ordinal);

        foreach (var block in blocks)
        {
            try
            {
                await _service.RaiseAsync(AlertSignals.ForBudget(block, _options, now), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogBudgetFailed(exception, block.TenantId);
            }
        }

        // An alert whose tenant is no longer being blocked has run its course.
        foreach (var open in await _store.FindOpenByKindAsync(AlertKind.BudgetExhausted, cancellationToken))
        {
            if (blocked.Contains(open.Fingerprint))
            {
                continue;
            }

            try
            {
                await _service.ResolveAsync(open.Fingerprint, now, "No calls have been blocked for budget since.", cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogResolveFailed(exception, open.Fingerprint);
            }
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 8201, Level = LogLevel.Debug, Message = "Skipped looking for alerts: another gateway instance is already doing it")]
    private partial void LogSkipped();

    [LoggerMessage(EventId = 8202, Level = LogLevel.Error, Message = "Could not raise or resolve the alert for anomaly {AnomalyId}; it will be tried again next round")]
    private partial void LogFailed(Exception exception, Guid anomalyId);

    [LoggerMessage(EventId = 8203, Level = LogLevel.Error, Message = "Could not raise the budget alert for {TenantId}; it will be tried again next round")]
    private partial void LogBudgetFailed(Exception exception, TenantId tenantId);

    [LoggerMessage(EventId = 8204, Level = LogLevel.Error, Message = "Could not resolve the alert {Fingerprint}; it will be tried again next round")]
    private partial void LogResolveFailed(Exception exception, string fingerprint);
}
