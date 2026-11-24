using System.Diagnostics;
using Fusebox.Application.Telemetry;
using Fusebox.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using static Fusebox.Infrastructure.Tests.RecordSamples;

namespace Fusebox.Infrastructure.Tests;

public class ChannelTelemetrySinkTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TelemetryMetrics _metrics;
    private readonly MeterCollector _meter;

    public ChannelTelemetrySinkTests()
    {
        _metrics = new TelemetryMetrics(_meterFactory);
        _meter = new MeterCollector(_meterFactory);
    }

    public void Dispose()
    {
        _meter.Dispose();
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    private ChannelTelemetrySink Create(int capacity) =>
        new(Options.Create(new TelemetryOptions { Store = TelemetryStoreKind.Postgres, QueueCapacity = capacity }), _metrics);

    [Fact]
    public void Accepts_records_and_counts_them()
    {
        var sink = Create(capacity: 10);

        Assert.True(sink.TryRecord(Record()));
        Assert.True(sink.TryRecord(Record()));

        Assert.Equal(2, _meter.Total("fusebox.telemetry.recorded"));
        Assert.Equal(0, _meter.TotalOfAnyReason("fusebox.telemetry.dropped"));
    }

    [Fact]
    public void Hands_records_to_the_reader_in_order()
    {
        var sink = Create(capacity: 10);
        var first = Record();
        var second = Record();
        sink.TryRecord(first);
        sink.TryRecord(second);

        Assert.True(sink.Reader.TryRead(out var one));
        Assert.True(sink.Reader.TryRead(out var two));
        Assert.Same(first, one);
        Assert.Same(second, two);
    }

    [Fact]
    public void Drops_and_counts_a_record_when_the_queue_is_full_instead_of_waiting()
    {
        var sink = Create(capacity: 2);
        sink.TryRecord(Record());
        sink.TryRecord(Record());

        var clock = Stopwatch.StartNew();
        var accepted = sink.TryRecord(Record());

        Assert.False(accepted);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, _meter.Total("fusebox.telemetry.dropped", TelemetryMetrics.QueueFull));
        Assert.Equal(2, _meter.Total("fusebox.telemetry.recorded"));
    }

    [Fact]
    public void Keeps_what_is_already_queued_when_it_drops_a_newer_record()
    {
        var sink = Create(capacity: 1);
        var kept = Record();
        sink.TryRecord(kept);

        sink.TryRecord(Record());

        Assert.True(sink.Reader.TryRead(out var read));
        Assert.Same(kept, read);
        Assert.False(sink.Reader.TryRead(out _));
    }

    [Fact]
    public void Stops_accepting_once_completed_but_lets_the_queue_be_emptied()
    {
        var sink = Create(capacity: 10);
        sink.TryRecord(Record());

        sink.Complete();

        Assert.False(sink.TryRecord(Record()));
        Assert.True(sink.Reader.TryRead(out _));
    }

    [Fact]
    public void Reports_how_many_records_are_waiting()
    {
        var sink = Create(capacity: 10);
        sink.TryRecord(Record());
        sink.TryRecord(Record());
        sink.TryRecord(Record());

        Assert.Equal(3, _meter.QueueLength());
    }

    [Fact]
    public void Many_threads_can_record_at_once_without_losing_or_inventing_records()
    {
        var sink = Create(capacity: 100_000);

        Parallel.For(0, 20, _ =>
        {
            for (var i = 0; i < 1_000; i++)
            {
                sink.TryRecord(Record());
            }
        });

        Assert.Equal(20_000, sink.Reader.Count);
        Assert.Equal(20_000, _meter.Total("fusebox.telemetry.recorded"));
    }
}

public class TelemetryWriterTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(Noon);
    private readonly TestMeterFactory _meterFactory = new();
    private readonly TelemetryMetrics _metrics;
    private readonly MeterCollector _meter;
    private readonly FakeTelemetryStore _store = new();
    private readonly List<TelemetryWriter> _writers = [];

    public TelemetryWriterTests()
    {
        _metrics = new TelemetryMetrics(_meterFactory);
        _meter = new MeterCollector(_meterFactory);
    }

    public void Dispose()
    {
        foreach (var writer in _writers)
        {
            writer.Dispose();
        }

        _meter.Dispose();
        _metrics.Dispose();
        GC.SuppressFinalize(this);
    }

    private (TelemetryWriter Writer, ChannelTelemetrySink Sink) Create(Action<TelemetryOptions>? tweak = null)
    {
        var options = new TelemetryOptions
        {
            Store = TelemetryStoreKind.Postgres,
            QueueCapacity = 1_000,
            MaxBatchSize = 3,
            FlushInterval = TimeSpan.FromSeconds(1),
            MaxWriteAttempts = 3,
        };
        tweak?.Invoke(options);

        var wrapped = Options.Create(options);
        var sink = new ChannelTelemetrySink(wrapped, _metrics);
        var writer = new TelemetryWriter(sink, _store, wrapped, _metrics, _time, NullLogger<TelemetryWriter>.Instance);
        _writers.Add(writer);
        return (writer, sink);
    }

    private static void Enqueue(ChannelTelemetrySink sink, int count)
    {
        for (var i = 0; i < count; i++)
        {
            sink.TryRecord(Record());
        }
    }

    [Fact]
    public async Task Saves_records_in_batches_no_bigger_than_the_limit()
    {
        var (writer, sink) = Create();
        Enqueue(sink, 7);

        await writer.StartAsync(CancellationToken.None);
        await TestWaiting.PumpUntilAsync(_time, () => _store.SavedRecords == 7);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal([3, 3, 1], _store.Batches.Select(batch => batch.Count));
        Assert.Equal(7, _meter.Total("fusebox.telemetry.written"));
    }

    [Fact]
    public async Task Saves_a_part_full_batch_once_the_flush_interval_has_passed()
    {
        var (writer, sink) = Create();
        Enqueue(sink, 2);

        await writer.StartAsync(CancellationToken.None);
        await TestWaiting.PumpUntilAsync(_time, () => _store.SavedRecords == 2);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, Assert.Single(_store.Batches).Count);
    }

    [Fact]
    public async Task Tries_a_failing_batch_again_and_loses_nothing_when_a_retry_succeeds()
    {
        var (writer, sink) = Create();
        _store.WriteFailures = 2;
        Enqueue(sink, 2);

        await writer.StartAsync(CancellationToken.None);
        await TestWaiting.PumpUntilAsync(_time, () => _store.SavedRecords == 2);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, _meter.Total("fusebox.telemetry.written"));
        Assert.Equal(0, _meter.TotalOfAnyReason("fusebox.telemetry.dropped"));
    }

    [Fact]
    public async Task Gives_up_on_a_batch_that_keeps_failing_counts_it_and_carries_on_with_the_next()
    {
        var (writer, sink) = Create();
        _store.WriteFailures = 3;
        Enqueue(sink, 2);

        await writer.StartAsync(CancellationToken.None);
        await TestWaiting.PumpUntilAsync(_time, () => _meter.Total("fusebox.telemetry.dropped", TelemetryMetrics.WriteFailed) == 2);

        Enqueue(sink, 1);
        await TestWaiting.PumpUntilAsync(_time, () => _store.SavedRecords == 1);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, _meter.Total("fusebox.telemetry.dropped", TelemetryMetrics.WriteFailed));
        Assert.Equal(1, _meter.Total("fusebox.telemetry.written"));
    }

    [Fact]
    public async Task Saves_everything_still_queued_when_it_is_stopped_without_waiting_for_the_flush_interval()
    {
        var (writer, sink) = Create();
        await writer.StartAsync(CancellationToken.None);
        Enqueue(sink, 2);
        await TestWaiting.UntilAsync(() => sink.Reader.Count == 0);

        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, _store.SavedRecords);
    }

    [Fact]
    public async Task Gives_up_at_shutdown_if_the_database_does_not_answer_in_time()
    {
        var (writer, sink) = Create();
        _store.Hang = true;
        Enqueue(sink, 2);
        await writer.StartAsync(CancellationToken.None);
        await TestWaiting.UntilAsync(() => sink.Reader.Count == 0);

        using var patience = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await writer.StopAsync(patience.Token);

        Assert.Equal(0, _store.SavedRecords);
        Assert.Equal(2, _meter.TotalOfAnyReason("fusebox.telemetry.dropped"));
    }

    [Fact]
    public async Task Does_nothing_when_telemetry_is_switched_off()
    {
        var (writer, sink) = Create(options => options.Store = TelemetryStoreKind.None);
        Enqueue(sink, 3);

        await writer.StartAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(0, _store.SavedRecords);
    }
}

public class TelemetryMaintenanceTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly FakeTimeProvider _time = new(Noon);
    private readonly FakeTelemetryStore _store = new();

    private TelemetryMaintenance Create(Action<TelemetryOptions>? tweak = null)
    {
        var options = new TelemetryOptions
        {
            Store = TelemetryStoreKind.Postgres,
            RollupInterval = TimeSpan.FromMinutes(1),
            MinuteRollupLookback = TimeSpan.FromMinutes(15),
            HourRollupLookback = TimeSpan.FromHours(3),
            RawRetention = TimeSpan.FromDays(30),
        };
        tweak?.Invoke(options);

        return new TelemetryMaintenance(_store, Options.Create(options), _time, NullLogger<TelemetryMaintenance>.Instance);
    }

    [Fact]
    public async Task Refreshes_the_recent_minute_and_hour_totals_each_round()
    {
        await Create().TickAsync(CancellationToken.None);

        Assert.Contains((RollupGranularity.Minute, Noon - TimeSpan.FromMinutes(15), Noon), _store.Rollups);
        Assert.Contains((RollupGranularity.Hour, Noon - TimeSpan.FromHours(3), Noon), _store.Rollups);
    }

    [Fact]
    public async Task Drops_expired_raw_records_on_the_first_round_and_then_about_once_an_hour()
    {
        var maintenance = Create();

        await maintenance.TickAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await maintenance.TickAsync(CancellationToken.None);
        Assert.Single(_store.Drops);
        Assert.Equal(Noon - TimeSpan.FromDays(30), _store.Drops[0]);

        _time.Advance(Hour);
        await maintenance.TickAsync(CancellationToken.None);
        Assert.Equal(2, _store.Drops.Count);
    }

    [Fact]
    public async Task A_failing_step_does_not_stop_the_others_or_the_next_round()
    {
        _store.RollupFailure = new InvalidOperationException("the database is down");
        var maintenance = Create();

        await maintenance.TickAsync(CancellationToken.None);

        // Both totals were attempted, and retention still ran.
        Assert.Equal(2, _store.Rollups.Count);
        Assert.Single(_store.Drops);

        _store.RollupFailure = null;
        _time.Advance(TimeSpan.FromMinutes(1));
        await maintenance.TickAsync(CancellationToken.None);
        Assert.Equal(4, _store.Rollups.Count);
    }

    [Fact]
    public async Task Carries_on_quietly_when_another_instance_holds_the_work()
    {
        _store.RollupGranted = false;

        await Create().TickAsync(CancellationToken.None);

        Assert.Equal(2, _store.Rollups.Count);
    }

    [Fact]
    public async Task Does_nothing_when_telemetry_is_switched_off()
    {
        var maintenance = Create(options => options.Store = TelemetryStoreKind.None);

        await maintenance.StartAsync(CancellationToken.None);
        await maintenance.StopAsync(CancellationToken.None);

        Assert.Empty(_store.Rollups);
    }
}

public class TelemetryOptionsValidatorTests
{
    private readonly TelemetryOptionsValidator _validator = new();

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.True(_validator.Validate(null, new TelemetryOptions()).Succeeded);
    }

    [Fact]
    public void Postgres_needs_a_connection_string()
    {
        Assert.True(_validator.Validate(null, new TelemetryOptions { Store = TelemetryStoreKind.Postgres }).Failed);
    }

    [Fact]
    public void Postgres_with_a_connection_string_is_valid()
    {
        var options = new TelemetryOptions
        {
            Store = TelemetryStoreKind.Postgres,
            Postgres = new Catalog.PostgresOptions { ConnectionString = "Host=localhost" },
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_lookback_shorter_than_the_interval_is_rejected_because_some_records_would_never_be_totalled()
    {
        var options = new TelemetryOptions { RollupInterval = TimeSpan.FromMinutes(10), MinuteRollupLookback = TimeSpan.FromMinutes(5) };

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void A_tiny_queue_is_rejected(int capacity)
    {
        Assert.True(_validator.Validate(null, new TelemetryOptions { QueueCapacity = capacity }).Failed);
    }

    [Fact]
    public void A_batch_size_of_zero_is_rejected()
    {
        Assert.True(_validator.Validate(null, new TelemetryOptions { MaxBatchSize = 0 }).Failed);
    }
}
