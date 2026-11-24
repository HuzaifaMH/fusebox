using Fusebox.Application.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Telemetry;

/// <summary>
/// Keeps the per-minute and per-hour totals up to date and removes raw records that have outlived their retention.
/// </summary>
/// <remarks>
/// Every gateway instance runs this, but the store lets only one do each piece of work at a time, so the work is not repeated.
/// Totals are recomputed from the raw records over a recent window rather than added to, which makes a repeated or
/// overlapping run harmless and lets records that arrive a little late still be counted.
/// </remarks>
internal sealed partial class TelemetryMaintenance(
    ITelemetryStore store,
    IOptions<TelemetryOptions> options,
    TimeProvider time,
    ILogger<TelemetryMaintenance> logger) : BackgroundService
{
    private static readonly TimeSpan RetentionCheckInterval = TimeSpan.FromHours(1);

    private readonly ILogger<TelemetryMaintenance> _logger = logger;
    private readonly TelemetryOptions _options = options.Value;
    private DateTimeOffset _nextRetentionCheck;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Store == TelemetryStoreKind.None)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.RollupInterval, time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The gateway is stopping.
        }
    }

    /// <summary>One round of maintenance. A failing step is logged and does not stop the others or the next round.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await RunAsync("per-minute totals", () => RollUpAsync(RollupGranularity.Minute, now - _options.MinuteRollupLookback, now, cancellationToken));
        await RunAsync("per-hour totals", () => RollUpAsync(RollupGranularity.Hour, now - _options.HourRollupLookback, now, cancellationToken));

        if (now >= _nextRetentionCheck)
        {
            _nextRetentionCheck = now + RetentionCheckInterval;
            await RunAsync("raw record retention", () => DropExpiredAsync(now, cancellationToken));
        }
    }

    private async Task RollUpAsync(RollupGranularity granularity, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        if (!await store.RollUpAsync(granularity, windowStart, windowEnd, cancellationToken))
        {
            LogSkipped(granularity);
        }
    }

    private async Task DropExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dropped = await store.DropRawBeforeAsync(now - _options.RawRetention, cancellationToken);

        if (dropped > 0)
        {
            LogDropped(dropped, _options.RawRetention);
        }
    }

#pragma warning disable CA1031 // One failing step must not stop the rest of the maintenance.
    private async Task RunAsync(string step, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStepFailed(exception, step);
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 5101, Level = LogLevel.Debug,
        Message = "Skipped the {Granularity} totals: another gateway instance is already computing them")]
    private partial void LogSkipped(RollupGranularity granularity);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information,
        Message = "Dropped {Days} days of raw request records older than {Retention}")]
    private partial void LogDropped(int days, TimeSpan retention);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Error,
        Message = "Telemetry maintenance step '{Step}' failed; it will be tried again next round")]
    private partial void LogStepFailed(Exception exception, string step);
}
