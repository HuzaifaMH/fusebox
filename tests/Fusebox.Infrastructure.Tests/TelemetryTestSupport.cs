using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Infrastructure.Tests;

internal static class RecordSamples
{
    /// <summary>Half a minute past noon on a fixed day, so a record sits inside one minute and one hour.</summary>
    public static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-03-04T12:00:30Z");

    public static RequestRecord Record(
        DateTimeOffset? at = null,
        string tenant = "acme",
        string route = "openai-chat",
        string? model = "m",
        RequestDisposition disposition = RequestDisposition.Forwarded,
        DenialReason? denial = null,
        int status = 200,
        int durationMs = 100,
        bool streaming = false,
        bool noUsage = false,
        long costMicroUsd = 50_000,
        long reservedMicroUsd = 200_000,
        bool repeat = false) =>
        new(
            Guid.NewGuid(),
            at ?? Noon,
            TenantId.Parse(tenant),
            route,
            model,
            disposition,
            denial,
            status,
            TimeSpan.FromMilliseconds(durationMs),
            streaming,
            noUsage ? null : new TokenUsage(10, 20),
            Money.FromMicroDollars(costMicroUsd),
            Money.FromMicroDollars(reservedMicroUsd),
            repeat);
}

/// <summary>A meter factory that needs no host, for building the metrics in a test.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    // Each meter is scoped to the factory that made it, so a collector can listen to one test's meter only.
    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Scope = this;
        return new Meter(options);
    }

    public void Dispose()
    {
    }
}

/// <summary>Adds up what a meter reports, by instrument and by the "reason" tag.</summary>
internal sealed class MeterCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _totals = new();
    private int _queueLength;

    public MeterCollector(TestMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    reason = tag.Value as string;
                }
            }

            _totals.AddOrUpdate(KeyOf(instrument.Name, reason), value, (_, current) => current + value);
        });

        _listener.SetMeasurementEventCallback<int>((_, value, _, _) => _queueLength = value);
        _listener.Start();
    }

    public long Total(string instrument, string? reason = null) => _totals.GetValueOrDefault(KeyOf(instrument, reason));

    public long TotalOfAnyReason(string instrument) => _totals.Where(pair => pair.Key.StartsWith(instrument + "|", StringComparison.Ordinal)).Sum(pair => pair.Value);

    public int QueueLength()
    {
        _listener.RecordObservableInstruments();
        return _queueLength;
    }

    public void Dispose() => _listener.Dispose();

    private static string KeyOf(string instrument, string? reason) => $"{instrument}|{reason}";
}

/// <summary>A store whose failures, delays and answers a test controls.</summary>
internal sealed class FakeTelemetryStore : ITelemetryStore
{
    private readonly Lock _gate = new();
    private readonly List<IReadOnlyList<RequestRecord>> _batches = [];
    private readonly List<(RollupGranularity Granularity, DateTimeOffset Start, DateTimeOffset End)> _rollups = [];
    private readonly List<DateTimeOffset> _drops = [];

    /// <summary>How many of the next writes fail.</summary>
    public int WriteFailures { get; set; }

    /// <summary>When true, writes never finish until cancelled.</summary>
    public bool Hang { get; set; }

    public Exception? RollupFailure { get; set; }

    public bool RollupGranted { get; set; } = true;

    public IReadOnlyList<IReadOnlyList<RequestRecord>> Batches
    {
        get
        {
            lock (_gate)
            {
                return [.. _batches];
            }
        }
    }

    public IReadOnlyList<(RollupGranularity Granularity, DateTimeOffset Start, DateTimeOffset End)> Rollups
    {
        get
        {
            lock (_gate)
            {
                return [.. _rollups];
            }
        }
    }

    public IReadOnlyList<DateTimeOffset> Drops
    {
        get
        {
            lock (_gate)
            {
                return [.. _drops];
            }
        }
    }

    public int SavedRecords => Batches.Sum(batch => batch.Count);

    public async ValueTask WriteAsync(IReadOnlyList<RequestRecord> batch, CancellationToken cancellationToken)
    {
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        lock (_gate)
        {
            if (WriteFailures > 0)
            {
                WriteFailures--;
                throw new InvalidOperationException("The database is down.");
            }

            _batches.Add([.. batch]);
        }
    }

    public ValueTask<bool> RollUpAsync(RollupGranularity granularity, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rollups.Add((granularity, windowStart, windowEnd));
        }

        return RollupFailure is null ? ValueTask.FromResult(RollupGranted) : throw RollupFailure;
    }

    public ValueTask<int> DropRawBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _drops.Add(cutoff);
        }

        return ValueTask.FromResult(0);
    }
}

internal static class TestWaiting
{
    /// <summary>Waits until the condition holds, moving fake time forward so timers and retry pauses can fire.</summary>
    public static async Task PumpUntilAsync(FakeTimeProvider time, Func<bool> condition, int timeoutSeconds = 15)
    {
        var clock = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds), "Timed out waiting for the condition.");
            time.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(5);
        }
    }

    /// <summary>Waits, in real time, until the condition holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutSeconds = 15)
    {
        var clock = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(timeoutSeconds), "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }
}
