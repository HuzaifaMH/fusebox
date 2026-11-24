using Fusebox.Application.Alerting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Alerting;

/// <summary>Looks for things to alert about on a schedule: open anomalies, ended anomalies and tenants over budget.</summary>
internal sealed partial class AlertSourceJob(
    AlertSourceRunner runner,
    IOptions<AlertOptions> options,
    TimeProvider time,
    ILogger<AlertSourceJob> logger) : BackgroundService
{
    private readonly ILogger<AlertSourceJob> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.SourceInterval, time);

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

#pragma warning disable CA1031 // Whatever goes wrong in a round, the next one must still run.
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await runner.RunAsync(time.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRoundFailed(exception);
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 8401, Level = LogLevel.Error, Message = "A round of looking for alerts failed; it will be tried again on the next one")]
    private partial void LogRoundFailed(Exception exception);
}

/// <summary>Sends the saved notifications that are due, on a schedule.</summary>
internal sealed partial class AlertDispatcherJob(
    AlertDispatcher dispatcher,
    IOptions<AlertOptions> options,
    TimeProvider time,
    ILogger<AlertDispatcherJob> logger) : BackgroundService
{
    private readonly ILogger<AlertDispatcherJob> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.DispatchInterval, time);

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

#pragma warning disable CA1031 // Whatever goes wrong in a round, the next one must still run.
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.DispatchAsync(time.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRoundFailed(exception);
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 8501, Level = LogLevel.Error, Message = "A round of sending alerts failed; it will be tried again on the next one")]
    private partial void LogRoundFailed(Exception exception);
}

/// <summary>
/// Pings an external monitor on a schedule, because Fusebox cannot report its own death. The monitor raises the alarm when the
/// pings stop, so a ping must mean something: it is sent only while the dispatcher that delivers alerts is alive.
/// </summary>
internal sealed partial class HeartbeatSender(
    HttpClient client,
    AlertDispatcher dispatcher,
    IOptions<AlertOptions> options,
    ILogger<HeartbeatSender> logger)
{
    private readonly ILogger<HeartbeatSender> _logger = logger;

    /// <summary>Pings the monitor if alerting is alive as of <paramref name="now"/>. Returns whether a ping was sent and accepted.</summary>
    public async Task<bool> PingAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        // Alive means a round of sending completed lately: a few rounds, and at least half a minute, to allow for a slow one.
        var allowedSilence = TimeSpan.FromTicks(Math.Max(settings.DispatchInterval.Ticks * 4, TimeSpan.FromSeconds(30).Ticks));

        if (dispatcher.LastRoundAt is not { } lastRound || now - lastRound > allowedSilence)
        {
            LogNotAlive();
            return false;
        }

#pragma warning disable CA1031 // A failed ping is for the monitor to notice, not for Fusebox to fail over.
        try
        {
            using var response = await client.GetAsync(settings.Heartbeat.Url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            LogRefused((int)response.StatusCode);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(exception);
            return false;
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(EventId = 8601, Level = LogLevel.Warning, Message = "Not sending the heartbeat: alert delivery has not completed a round lately, so Fusebox is not fully alive")]
    private partial void LogNotAlive();

    [LoggerMessage(EventId = 8602, Level = LogLevel.Warning, Message = "The heartbeat monitor answered {Status}")]
    private partial void LogRefused(int status);

    [LoggerMessage(EventId = 8603, Level = LogLevel.Warning, Message = "Could not reach the heartbeat monitor")]
    private partial void LogFailed(Exception exception);
}

internal sealed class HeartbeatJob(
    HeartbeatSender sender,
    IOptions<AlertOptions> options,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled || !settings.Heartbeat.IsConfigured)
        {
            return;
        }

        using var timer = new PeriodicTimer(settings.Heartbeat.Interval, time);

        try
        {
            do
            {
                await sender.PingAsync(time.GetUtcNow(), stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The gateway is stopping.
        }
    }
}
