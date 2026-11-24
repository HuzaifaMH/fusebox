using Fusebox.Application.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Telemetry;

/// <summary>
/// Takes records off the queue and saves them in batches, off the request path.
/// </summary>
/// <remarks>
/// A batch is saved when it is full or when its first record has waited <see cref="TelemetryOptions.FlushInterval"/>,
/// whichever comes first. A failing batch is retried with growing pauses; while it is stuck the queue fills and then
/// drops new records, which are counted. On shutdown the queue is flushed, so a normal deploy loses nothing.
/// </remarks>
internal sealed partial class TelemetryWriter(
    ChannelTelemetrySink queue,
    ITelemetryStore store,
    IOptions<TelemetryOptions> options,
    TelemetryMetrics metrics,
    TimeProvider time,
    ILogger<TelemetryWriter> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WindDownLimit = TimeSpan.FromSeconds(5);

    private readonly ILogger<TelemetryWriter> _logger = logger;
    private readonly TelemetryOptions _options = options.Value;

    // Cancelled only if shutdown runs out of time, to stop retrying and let the process exit.
    private readonly CancellationTokenSource _giveUp = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.Store == TelemetryStoreKind.None)
        {
            return;
        }

        var reader = queue.Reader;
        var batch = new List<RequestRecord>(_options.MaxBatchSize);

        // Ends when the queue has been completed and emptied, which only happens at shutdown.
        while (await reader.WaitToReadAsync(CancellationToken.None))
        {
            batch.Clear();
            await FillBatchAsync(batch);

            if (batch.Count > 0)
            {
                await WriteWithRetryAsync(batch);
            }
        }
    }

    /// <summary>Stops taking new records, saves what is queued, then stops.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();

        try
        {
            if (ExecuteTask is { } running)
            {
                await running.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            LogShutdownTimedOut(queue.Reader.Count);
            await _giveUp.CancelAsync();
            await WaitForWriterToWindDownAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    // Once told to give up, the writer ends within moments, having counted what it lost. Wait for that, but not forever.
    private async Task WaitForWriterToWindDownAsync()
    {
        try
        {
            if (ExecuteTask is { } running)
            {
                await running.WaitAsync(WindDownLimit, time, CancellationToken.None);
            }
        }
        catch (TimeoutException)
        {
            // It did not stop in time. Shutdown goes ahead regardless.
        }
    }

    public override void Dispose()
    {
        _giveUp.Dispose();
        base.Dispose();
    }

    // Reads what is waiting, then lets a part-full batch wait a little longer for company.
    private async Task FillBatchAsync(List<RequestRecord> batch)
    {
        Drain(batch);

        if (batch.Count >= _options.MaxBatchSize)
        {
            return;
        }

        using var window = new CancellationTokenSource(_options.FlushInterval, time);

        try
        {
            while (batch.Count < _options.MaxBatchSize && await queue.Reader.WaitToReadAsync(window.Token))
            {
                Drain(batch);
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed: save what there is.
        }
    }

    private void Drain(List<RequestRecord> batch)
    {
        while (batch.Count < _options.MaxBatchSize && queue.Reader.TryRead(out var record))
        {
            batch.Add(record);
        }
    }

#pragma warning disable CA1031 // Whatever makes a save fail, the writer must keep going.
    private async Task WriteWithRetryAsync(List<RequestRecord> batch)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.WriteAsync(batch, _giveUp.Token);
                metrics.Written.Add(batch.Count);
                return;
            }
            catch (OperationCanceledException) when (_giveUp.IsCancellationRequested)
            {
                Drop(batch, "shutdown ran out of time");
                return;
            }
            catch (Exception exception)
            {
                if (attempt >= _options.MaxWriteAttempts)
                {
                    LogGaveUp(exception, batch.Count, attempt);
                    Drop(batch, TelemetryMetrics.WriteFailed);
                    return;
                }

                var delay = RetryDelay(attempt);
                LogRetrying(exception, batch.Count, attempt, delay);

                try
                {
                    await Task.Delay(delay, time, _giveUp.Token);
                }
                catch (OperationCanceledException)
                {
                    Drop(batch, "shutdown ran out of time");
                    return;
                }
            }
        }
    }
#pragma warning restore CA1031

    private void Drop(List<RequestRecord> batch, string reason) =>
        metrics.Dropped.Add(batch.Count, new KeyValuePair<string, object?>("reason", reason));

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromTicks(Math.Min(FirstRetryDelay.Ticks << Math.Min(attempt - 1, 10), LongestRetryDelay.Ticks));

    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "Could not save a batch of {Records} request records (attempt {Attempt}); trying again in {Delay}")]
    private partial void LogRetrying(Exception exception, int records, int attempt, TimeSpan delay);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Error,
        Message = "Gave up saving a batch of {Records} request records after {Attempts} attempts; they are lost")]
    private partial void LogGaveUp(Exception exception, int records, int attempts);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Error,
        Message = "Shutdown ran out of time with {Queued} request records still queued; they are lost")]
    private partial void LogShutdownTimedOut(int queued);
}
