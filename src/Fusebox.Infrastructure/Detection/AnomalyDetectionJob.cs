using Fusebox.Application.Detection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Detection;

/// <summary>Runs a round of anomaly detection on a schedule.</summary>
/// <remarks>
/// Every gateway instance runs this, but the store lets only one round proceed at a time.
/// A failing round is logged and the next one tries again, so a database outage pauses detection, not the gateway.
/// </remarks>
internal sealed partial class AnomalyDetectionJob(
    AnomalyDetector detector,
    IOptions<DetectionOptions> options,
    TimeProvider time,
    ILogger<AnomalyDetectionJob> logger) : BackgroundService
{
    private readonly ILogger<AnomalyDetectionJob> _logger = logger;
    private readonly DetectionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.EvaluationInterval, time);

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
            await detector.RunAsync(time.GetUtcNow(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRoundFailed(exception);
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 6101, Level = LogLevel.Error, Message = "A round of anomaly detection failed; it will be tried again on the next one")]
    private partial void LogRoundFailed(Exception exception);
}
