using Fusebox.Application.Detection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Diagnosis;

/// <summary>
/// One round of diagnosis: finds anomalies that have none yet, works out what changed, explains it, and saves the result.
/// Each anomaly is diagnosed once. Scheduling is somebody else's job, so a round can be replayed in a test.
/// </summary>
public sealed partial class DiagnosisRunner
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private readonly IDiagnosisStore _store;
    private readonly IEvidenceSource _source;
    private readonly NarrationService _narrator;
    private readonly DiagnosisOptions _options;
    private readonly DetectionOptions _detection;
    private readonly EvidenceEngine _engine;
    private readonly ILogger<DiagnosisRunner> _logger;

    public DiagnosisRunner(
        IDiagnosisStore store,
        IEvidenceSource source,
        NarrationService narrator,
        IOptions<DiagnosisOptions> options,
        IOptions<DetectionOptions> detection,
        ILogger<DiagnosisRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(detection);

        _store = store;
        _source = source;
        _narrator = narrator;
        _options = options.Value;
        _detection = detection.Value;
        _engine = new EvidenceEngine(_options);
        _logger = logger;
    }

    /// <summary>Runs one round as of <paramref name="now"/>. Does nothing if another gateway instance is already running one.</summary>
    public async ValueTask RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ran = await _store.TryRunExclusivelyAsync(token => DiagnoseAsync(now, token), cancellationToken);

        if (!ran)
        {
            LogSkipped();
        }
    }

    private async Task DiagnoseAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await _store.LoadUndiagnosedAsync(now - _options.MaxAnomalyAge, _options.BatchSize, cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        var monthStart = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var narrations = _options.NarratorEnabled ? await _store.CountLlmNarrationsAsync(monthStart, cancellationToken) : 0;

        foreach (var anomaly in pending)
        {
            try
            {
                var narration = await DiagnoseOneAsync(anomaly, now, narrations < _options.MaxNarrationsPerMonth, cancellationToken);

                if (narration.Source == NarrationSource.Llm)
                {
                    narrations++;
                }
            }
#pragma warning disable CA1031 // One anomaly that cannot be diagnosed must not stop the others. It is tried again next round.
            catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(exception, anomaly.AnomalyId);
            }
        }
    }

    private async Task<Narration> DiagnoseOneAsync(AnomalyToDiagnose anomaly, DateTimeOffset now, bool allowLlm, CancellationToken cancellationToken)
    {
        // The window the detector judged when it found the anomaly, and the stretch of time before it.
        var windowEnd = Truncate(anomaly.OpenedAt - _detection.Lag);
        var windowStart = windowEnd - (_detection.WindowMinutes * OneMinute);
        var beforeStart = windowStart - (_options.BeforeMinutes * OneMinute);

        var window = await _source.LoadPeriodAsync(anomaly.Series, windowStart, windowEnd, cancellationToken);
        var before = await _source.LoadPeriodAsync(anomaly.Series, beforeStart, windowStart, cancellationToken);

        var evidence = _engine.Analyze(anomaly.Series, anomaly.Signal, windowStart, windowEnd, window, before);
        var narration = await _narrator.NarrateAsync(evidence, allowLlm, cancellationToken);

        await _store.SaveAsync(new DiagnosisRecord(anomaly.AnomalyId, now, evidence, narration), cancellationToken);
        LogDiagnosed(anomaly.Series, anomaly.Signal, narration.Source, narration.Confidence);

        return narration;
    }

    private static DateTimeOffset Truncate(DateTimeOffset instant) =>
        new(instant.UtcDateTime.Ticks - (instant.UtcDateTime.Ticks % OneMinute.Ticks), TimeSpan.Zero);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Information, Message = "Diagnosed the anomaly on {Series} ({Signal}) using the {Source}, with {Confidence} confidence")]
    private partial void LogDiagnosed(SeriesKey series, Signal signal, NarrationSource source, NarrationConfidence confidence);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Error, Message = "Could not diagnose anomaly {AnomalyId}; it will be tried again next round")]
    private partial void LogFailed(Exception exception, Guid anomalyId);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Debug, Message = "Skipped diagnosis: another gateway instance is already running it")]
    private partial void LogSkipped();
}
