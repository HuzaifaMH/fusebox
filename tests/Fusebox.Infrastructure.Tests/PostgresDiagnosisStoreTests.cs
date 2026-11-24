using System.Text.Json;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Detection.Postgres;
using Fusebox.Infrastructure.Diagnosis;
using Fusebox.Infrastructure.Diagnosis.Postgres;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static Fusebox.Infrastructure.Tests.RecordSamples;

namespace Fusebox.Infrastructure.Tests;

/// <summary>The diagnosis store, and the whole chain from raw requests to a saved diagnosis, on a real PostgreSQL.</summary>
[Collection(SharedPostgres.Name)]
public class PostgresDiagnosisStoreTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-03-04T02:00:00Z");
    private static readonly SeriesKey Acme = new(TenantId.Parse("acme"), "openai-chat");

    private static readonly Signal[] FourSignals = [Signal.Spend, Signal.Volume, Signal.ErrorRate, Signal.LatencyP95];

    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is not null)
        {
            await RunAsync("DROP TABLE IF EXISTS anomaly_diagnoses, requests, rollups_minute, rollups_hour, anomalies CASCADE");
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private IOptions<TelemetryOptions> TelemetryOptions() => Options.Create(new TelemetryOptions
    {
        Store = TelemetryStoreKind.Postgres,
        Postgres = new PostgresOptions { ConnectionString = postgres.ConnectionString! },
    });

    private PostgresDiagnosisStore CreateStore()
    {
        var store = new PostgresDiagnosisStore(TelemetryOptions());
        _disposables.Add(store);
        return store;
    }

    private PostgresAnomalyStore CreateAnomalyStore()
    {
        var store = new PostgresAnomalyStore(TelemetryOptions());
        _disposables.Add(store);
        return store;
    }

    private PostgresTelemetryStore CreateTelemetryStore()
    {
        var store = new PostgresTelemetryStore(TelemetryOptions(), NullLogger<PostgresTelemetryStore>.Instance);
        _disposables.Add(store);
        return store;
    }

    private async Task RunAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static IEnumerable<RequestRecord> Minute(DateTimeOffset minute, int count, long costMicroUsd, string model) =>
        Enumerable.Range(0, count).Select(i => Record(at: minute.AddMilliseconds(i * 500), model: model, costMicroUsd: costMicroUsd, durationMs: 100 + (i % 400)));

    private async Task<OpenAnomaly> OpenAnomalyAsync(DateTimeOffset openedAt, Signal signal = Signal.Spend)
    {
        var anomaly = new OpenAnomaly(Guid.NewGuid(), Acme, signal, openedAt, 200_000, BaselineBasis.Recent, 1_000_000, 1_000_000, 5, 0, openedAt);
        await CreateAnomalyStore().SaveAsync(new AnomalyOpenedChange(anomaly), CancellationToken.None);
        return anomaly;
    }

    private static DiagnosisRecord Diagnosis(OpenAnomaly anomaly, NarrationSource source = NarrationSource.Template, DateTimeOffset? at = null)
    {
        var evidence = new Evidence(
            Acme,
            Signal.Spend,
            T0,
            T0.AddMinutes(5),
            5,
            60,
            [
                new EvidenceItem("spend.total", EvidenceKind.Context, "Spend per minute", "$/min", 0.2, 1.2, null),
                new EvidenceItem("spend.volume", EvidenceKind.Factor, "Billable calls per minute", "calls/min", 100, 600, 1.0),
            ],
            0,
            false);

        var narration = new Narration(
            "Spend rose 6x because calls went up.",
            NarrationConfidence.High,
            ["spend.volume"],
            "Check for a retry loop.",
            source,
            source == NarrationSource.Llm ? "test-model" : null,
            source == NarrationSource.Template ? "the model took too long" : null);

        return new DiagnosisRecord(anomaly.Id, at ?? T0.AddMinutes(7), evidence, narration);
    }

    [SkippableFact]
    public async Task Reads_one_periods_totals_per_model_and_counts_the_minutes_with_traffic()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var telemetry = CreateTelemetryStore();
        await telemetry.WriteAsync(
            [.. Minute(T0, 6, 2_000, "mini"), .. Minute(T0, 4, 20_000, "big"), .. Minute(T0.AddMinutes(2), 10, 2_000, "mini")],
            CancellationToken.None);
        await telemetry.RollUpAsync(RollupGranularity.Minute, T0, T0.AddMinutes(10), CancellationToken.None);

        var period = await CreateStore().LoadPeriodAsync(Acme, T0, T0.AddMinutes(5), CancellationToken.None);

        Assert.Equal(2, period.Minutes);
        Assert.Equal(5, period.PeriodMinutes);
        Assert.Equal(16, period.ByModel["mini"].Requests);
        Assert.Equal(16 * 2_000, period.ByModel["mini"].CostMicroUsd);
        Assert.Equal(4, period.ByModel["big"].Forwarded);
        Assert.Equal(80_000, period.ByModel["big"].CostMicroUsd);
        Assert.Equal(20, period.Requests);
        Assert.Equal(LatencyBuckets.Count, period.LatencyBuckets.Count);
        Assert.Equal(20, period.LatencyBuckets.Sum());
    }

    [SkippableFact]
    public async Task Reads_nothing_for_another_series_or_another_period()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var telemetry = CreateTelemetryStore();
        await telemetry.WriteAsync([.. Minute(T0, 5, 2_000, "mini")], CancellationToken.None);
        await telemetry.RollUpAsync(RollupGranularity.Minute, T0, T0.AddMinutes(1), CancellationToken.None);
        var store = CreateStore();

        var otherSeries = await store.LoadPeriodAsync(new SeriesKey(TenantId.Parse("globex"), "openai-chat"), T0, T0.AddMinutes(5), CancellationToken.None);
        var otherPeriod = await store.LoadPeriodAsync(Acme, T0.AddHours(1), T0.AddHours(1).AddMinutes(5), CancellationToken.None);

        Assert.Empty(otherSeries.ByModel);
        Assert.Empty(otherPeriod.ByModel);
        Assert.Equal(0, otherSeries.Minutes);
    }

    [SkippableFact]
    public async Task Finds_the_anomalies_that_have_no_diagnosis_oldest_first_and_within_the_age_and_limit()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var tooOld = await OpenAnomalyAsync(T0.AddMinutes(-90), Signal.Volume);
        var first = await OpenAnomalyAsync(T0.AddMinutes(1), Signal.ErrorRate);
        var second = await OpenAnomalyAsync(T0.AddMinutes(2), Signal.Spend);
        var done = await OpenAnomalyAsync(T0.AddMinutes(3), Signal.LatencyP95);
        var store = CreateStore();
        await store.SaveAsync(Diagnosis(done), CancellationToken.None);

        var pending = await store.LoadUndiagnosedAsync(T0.AddMinutes(-60), 10, CancellationToken.None);

        Assert.Equal([first.Id, second.Id], pending.Select(p => p.AnomalyId));
        Assert.Equal(Signal.ErrorRate, pending[0].Signal);
        Assert.Equal(Acme, pending[0].Series);
        Assert.Equal(T0.AddMinutes(1), pending[0].OpenedAt);
        Assert.Equal([first.Id], (await store.LoadUndiagnosedAsync(T0.AddMinutes(-60), 1, CancellationToken.None)).Select(p => p.AnomalyId));
        Assert.DoesNotContain(tooOld.Id, pending.Select(p => p.AnomalyId));
    }

    [SkippableFact]
    public async Task Keeps_the_diagnosis_with_its_evidence()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var anomaly = await OpenAnomalyAsync(T0);
        var record = Diagnosis(anomaly);

        await CreateStore().SaveAsync(record, CancellationToken.None);

        Assert.Equal("template", await ScalarAsync<string>("SELECT source FROM anomaly_diagnoses"));
        Assert.Equal("high", await ScalarAsync<string>("SELECT confidence FROM anomaly_diagnoses"));
        Assert.Equal("Spend rose 6x because calls went up.", await ScalarAsync<string>("SELECT diagnosis FROM anomaly_diagnoses"));
        Assert.Equal("the model took too long", await ScalarAsync<string>("SELECT fallback_reason FROM anomaly_diagnoses"));
        Assert.Equal(["spend.volume"], await ScalarAsync<string[]>("SELECT evidence_ids FROM anomaly_diagnoses"));
        Assert.Equal(record.Evidence.Items.Count, await ScalarAsync<int>("SELECT jsonb_array_length(evidence) FROM anomaly_diagnoses"));

        using var evidence = JsonDocument.Parse(await ScalarAsync<string>("SELECT evidence::text FROM anomaly_diagnoses"));
        var volume = evidence.RootElement.EnumerateArray().Single(e => e.GetProperty("Id").GetString() == "spend.volume");
        Assert.Equal("Factor", volume.GetProperty("Kind").GetString());
        Assert.InRange(volume.GetProperty("ShareOfChange").GetDouble(), 0.99, 1.01);
    }

    [SkippableFact]
    public async Task Saving_a_second_diagnosis_for_the_same_anomaly_changes_nothing()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var anomaly = await OpenAnomalyAsync(T0);
        var store = CreateStore();
        await store.SaveAsync(Diagnosis(anomaly), CancellationToken.None);

        await store.SaveAsync(Diagnosis(anomaly, NarrationSource.Llm), CancellationToken.None);

        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM anomaly_diagnoses"));
        Assert.Equal("template", await ScalarAsync<string>("SELECT source FROM anomaly_diagnoses"));
    }

    [SkippableFact]
    public async Task A_diagnosis_needs_an_anomaly_to_belong_to()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var orphan = new OpenAnomaly(Guid.NewGuid(), Acme, Signal.Spend, T0, 1, BaselineBasis.Recent, 1, 1, 1, 0, T0);

        await Assert.ThrowsAsync<PostgresException>(async () => await CreateStore().SaveAsync(Diagnosis(orphan), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Counts_only_the_llm_diagnoses_saved_since_the_date_asked_about()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var store = CreateStore();
        var anomalies = new List<OpenAnomaly>();
        foreach (var signal in FourSignals)
        {
            anomalies.Add(await OpenAnomalyAsync(T0, signal));
        }

        await store.SaveAsync(Diagnosis(anomalies[0], NarrationSource.Llm, T0.AddDays(2)), CancellationToken.None);
        await store.SaveAsync(Diagnosis(anomalies[1], NarrationSource.Llm, T0.AddDays(3)), CancellationToken.None);
        await store.SaveAsync(Diagnosis(anomalies[2], NarrationSource.Llm, T0.AddDays(-40)), CancellationToken.None);
        await store.SaveAsync(Diagnosis(anomalies[3], NarrationSource.Template, T0.AddDays(2)), CancellationToken.None);

        Assert.Equal(2, await store.CountLlmNarrationsAsync(T0, CancellationToken.None));
        Assert.Equal(1, await store.CountLlmNarrationsAsync(T0.AddDays(3), CancellationToken.None));
        Assert.Equal(0, await store.CountLlmNarrationsAsync(T0.AddDays(10), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Lets_only_one_gateway_instance_diagnose_at_a_time()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var first = CreateStore();
        var second = CreateStore();
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var running = first.TryRunExclusivelyAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        }, CancellationToken.None).AsTask();
        await started.Task;

        var refused = !await second.TryRunExclusivelyAsync(_ => Task.CompletedTask, CancellationToken.None);
        release.SetResult();

        Assert.True(await running);
        Assert.True(refused);
        Assert.True(await second.TryRunExclusivelyAsync(_ => Task.CompletedTask, CancellationToken.None));
    }

    /// <summary>
    /// Every request goes to "mini" at $0.002 for an hour, then to "big" at ten times the price for five minutes.
    /// Each is found by detection, diagnosed from the stored totals, and saved.
    /// </summary>
    private async Task<(PostgresAnomalyStore Anomalies, DateTimeOffset RoundAt)> SeedModelSwapAndDetectAsync()
    {
        var telemetry = CreateTelemetryStore();
        var records = new List<RequestRecord>();
        for (var minute = -45; minute < 15; minute++)
        {
            var swapped = minute is >= 0 and < 5;
            records.AddRange(Minute(T0.AddMinutes(minute), 100, swapped ? 20_000 : 2_000, swapped ? "big" : "mini"));
        }

        foreach (var batch in records.Chunk(1_000))
        {
            await telemetry.WriteAsync(batch, CancellationToken.None);
        }

        await telemetry.RollUpAsync(RollupGranularity.Minute, T0.AddHours(-1), T0.AddMinutes(20), CancellationToken.None);

        var anomalies = CreateAnomalyStore();
        var detector = new AnomalyDetector(
            anomalies,
            [],
            Options.Create(new DetectionOptions { Enabled = true }),
            NullLogger<AnomalyDetector>.Instance);
        await detector.RunAsync(T0.AddMinutes(6), CancellationToken.None);

        return (anomalies, T0.AddMinutes(7));
    }

    private static DiagnosisRunner CreateRunner(PostgresDiagnosisStore store, DiagnosisOptions options, ILlmClient? llm = null)
    {
        var narrator = new NarrationService(Options.Create(options), NullLogger<NarrationService>.Instance, llm);

        return new DiagnosisRunner(
            store,
            store,
            narrator,
            Options.Create(options),
            Options.Create(new DetectionOptions { Enabled = true }),
            NullLogger<DiagnosisRunner>.Instance);
    }

    [SkippableFact]
    public async Task From_raw_requests_to_a_detected_anomaly_to_a_saved_diagnosis_that_names_the_model_swap()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var (anomalies, roundAt) = await SeedModelSwapAndDetectAsync();
        var anomaly = Assert.Single(await anomalies.LoadOpenAsync(CancellationToken.None));
        var store = CreateStore();

        await CreateRunner(store, new DiagnosisOptions { Enabled = true }).RunAsync(roundAt, CancellationToken.None);

        Assert.Equal(anomaly.Id, await ScalarAsync<Guid>("SELECT anomaly_id FROM anomaly_diagnoses"));
        Assert.Equal("template", await ScalarAsync<string>("SELECT source FROM anomaly_diagnoses"));

        var diagnosis = await ScalarAsync<string>("SELECT diagnosis FROM anomaly_diagnoses");
        Assert.Contains("Traffic shifting between models", diagnosis, StringComparison.OrdinalIgnoreCase);
        // The detector judged five minutes, four of them after the swap, so the big model carried 80% of the tokens.
        Assert.Contains("big: 0% -> 80% of tokens", diagnosis, StringComparison.Ordinal);
        Assert.Contains("spend.modelMix", await ScalarAsync<string[]>("SELECT evidence_ids FROM anomaly_diagnoses"));
        Assert.Contains("moved traffic to a different model", await ScalarAsync<string>("SELECT remediation FROM anomaly_diagnoses"), StringComparison.Ordinal);
        Assert.Empty(await store.LoadUndiagnosedAsync(T0.AddHours(-1), 10, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Diagnoses_each_anomaly_once_however_many_rounds_run()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var (_, roundAt) = await SeedModelSwapAndDetectAsync();
        var runner = CreateRunner(CreateStore(), new DiagnosisOptions { Enabled = true });

        await runner.RunAsync(roundAt, CancellationToken.None);
        await runner.RunAsync(roundAt.AddMinutes(1), CancellationToken.None);
        await runner.RunAsync(roundAt.AddMinutes(2), CancellationToken.None);

        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM anomaly_diagnoses"));
    }

    [SkippableFact]
    public async Task Saves_an_llm_explanation_when_it_is_enabled_and_what_it_says_checks_out()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var (_, roundAt) = await SeedModelSwapAndDetectAsync();
        var llm = new EvidenceQuotingLlm();

        await CreateRunner(CreateStore(), new DiagnosisOptions { Enabled = true, NarratorEnabled = true }, llm).RunAsync(roundAt, CancellationToken.None);

        Assert.Equal(1, llm.Calls);
        Assert.Equal("llm", await ScalarAsync<string>("SELECT source FROM anomaly_diagnoses"));
        Assert.Equal("quoting-model", await ScalarAsync<string>("SELECT model FROM anomaly_diagnoses"));
        Assert.Contains("traffic moved to the big model", await ScalarAsync<string>("SELECT diagnosis FROM anomaly_diagnoses"), StringComparison.Ordinal);
        Assert.Equal(1, await CreateStore().CountLlmNarrationsAsync(T0.AddDays(-1), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Falls_back_to_the_template_and_records_why_when_the_llm_makes_up_a_number()
    {
        Skip.If(postgres.ConnectionString is null, postgres.UnavailableReason);
        var (_, roundAt) = await SeedModelSwapAndDetectAsync();
        var llm = new EvidenceQuotingLlm { InventedNumber = "$12,345" };

        await CreateRunner(CreateStore(), new DiagnosisOptions { Enabled = true, NarratorEnabled = true }, llm).RunAsync(roundAt, CancellationToken.None);

        Assert.Equal("template", await ScalarAsync<string>("SELECT source FROM anomaly_diagnoses"));
        Assert.Contains("refused", await ScalarAsync<string>("SELECT fallback_reason FROM anomaly_diagnoses"), StringComparison.Ordinal);
        Assert.DoesNotContain("12,345", await ScalarAsync<string>("SELECT diagnosis FROM anomaly_diagnoses"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An LLM that reads the evidence it is sent and writes an honest explanation quoting it,
    /// so the chain can be tested without a real model.
    /// </summary>
    private sealed class EvidenceQuotingLlm : ILlmClient
    {
        private static readonly string[] Cited = ["spend.total", "spend.modelMix"];

        public string? InventedNumber { get; set; }

        public int Calls { get; private set; }

        public ValueTask<LlmReply> CompleteJsonAsync(LlmPrompt prompt, CancellationToken cancellationToken)
        {
            Calls++;

            using var json = JsonDocument.Parse(prompt.User);
            var total = json.RootElement.GetProperty("evidence").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "spend.total");
            var ratio = total.GetProperty("changeRatio").GetDouble();

            var diagnosis = $"Spend per minute rose {ratio:0.#}x because traffic moved to the big model.";
            if (InventedNumber is not null)
            {
                diagnosis += $" That cost an extra {InventedNumber}.";
            }

            var reply = JsonSerializer.Serialize(new
            {
                diagnosis,
                confidence = "high",
                evidenceIds = Cited,
                remediation = "Check whether a configuration change moved traffic to a pricier model.",
            });

            return ValueTask.FromResult(new LlmReply(reply, "quoting-model"));
        }
    }
}
