using System.Threading.Channels;
using Fusebox.Application.Alerting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Alerting;

/// <summary>
/// Where an event-driven source, such as the budget store going down, leaves an alert. Posting is a single non-blocking
/// call, because what notices the problem is serving requests and must not wait on a database.
/// </summary>
internal sealed class AlertInbox(IOptions<AlertOptions> options) : IAlertInbox
{
    private readonly Channel<QueuedAlert> _channel = Channel.CreateBounded<QueuedAlert>(new BoundedChannelOptions(1_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });

    public ChannelReader<QueuedAlert> Reader => _channel.Reader;

    public bool TryPost(QueuedAlert alert) => options.Value.Enabled && _channel.Writer.TryWrite(alert);

    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>Takes alerts from the inbox and saves them, retrying until the database accepts them.</summary>
internal sealed partial class AlertInboxWriter(
    AlertInbox inbox,
    AlertService service,
    IOptions<AlertOptions> options,
    TimeProvider time,
    ILogger<AlertInboxWriter> logger) : BackgroundService
{
    private const int MaxAttempts = 12;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestDelay = TimeSpan.FromSeconds(30);

    private readonly ILogger<AlertInboxWriter> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            while (await inbox.Reader.WaitToReadAsync(stoppingToken))
            {
                while (inbox.Reader.TryRead(out var alert))
                {
                    await SaveAsync(alert, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The gateway is stopping.
        }
    }

#pragma warning disable CA1031 // The database may be down at the very moment an alert is raised. Try again until it is not.
    internal async Task SaveAsync(QueuedAlert alert, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                switch (alert)
                {
                    case QueuedRaise raise:
                        await service.RaiseAsync(raise.Signal, cancellationToken);
                        break;

                    case QueuedResolve resolve:
                        await service.ResolveAsync(resolve.Fingerprint, resolve.At, resolve.Note, cancellationToken);
                        break;
                }

                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (attempt >= MaxAttempts)
                {
                    LogGaveUp(exception, attempt);
                    return;
                }

                LogRetrying(exception, attempt);
                var delay = TimeSpan.FromTicks(Math.Min(FirstDelay.Ticks << Math.Min(attempt - 1, 10), LongestDelay.Ticks));
                await Task.Delay(delay, time, cancellationToken);
            }
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 8301, Level = LogLevel.Warning, Message = "Could not save a queued alert (attempt {Attempt}); trying again")]
    private partial void LogRetrying(Exception exception, int attempt);

    [LoggerMessage(EventId = 8302, Level = LogLevel.Error, Message = "Gave up saving a queued alert after {Attempts} attempts; it is lost")]
    private partial void LogGaveUp(Exception exception, int attempts);
}
