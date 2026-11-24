using Fusebox.Application.Diagnosis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Diagnosis;

/// <summary>Picks up anomalies that have no diagnosis on a schedule and diagnoses them.</summary>
/// <remarks>
/// Every gateway instance runs this, but the store lets only one round proceed at a time. A failing round is logged
/// and the next one tries again, so a database outage pauses diagnosis and nothing else.
/// </remarks>
internal sealed partial class DiagnosisJob(
    DiagnosisRunner runner,
    IOptions<DiagnosisOptions> options,
    TimeProvider time,
    ILogger<DiagnosisJob> logger) : BackgroundService
{
    private readonly ILogger<DiagnosisJob> _logger = logger;
    private readonly DiagnosisOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval, time);

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

    [LoggerMessage(EventId = 7201, Level = LogLevel.Error, Message = "A round of diagnosis failed; it will be tried again on the next one")]
    private partial void LogRoundFailed(Exception exception);
}
