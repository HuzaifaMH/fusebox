using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Tests;

public class DiagnosisRunnerTests
{
    private static readonly DateTimeOffset T0 = Traffic.Origin.AddHours(2);

    private readonly FakeDiagnosisStore _store = new();
    private readonly FakeEvidenceSource _source = new();
    private readonly StubLlm _llm = new();
    private readonly DiagnosisOptions _options = new();
    private readonly DetectionOptions _detection = new();

    private DiagnosisRunner Create()
    {
        var options = Options.Create(_options);
        var narrator = new NarrationService(options, NullLogger<NarrationService>.Instance, _llm);

        return new DiagnosisRunner(_store, _source, narrator, options, Options.Create(_detection), NullLogger<DiagnosisRunner>.Instance);
    }

    private static AnomalyToDiagnose Anomaly(Signal signal = Signal.Spend, string tenant = "acme", int minutesAfter = 6) =>
        new(Guid.NewGuid(), new SeriesKey(Fusebox.Domain.TenantId.Parse(tenant), "openai-chat"), signal, T0.AddMinutes(minutesAfter));

    [Fact]
    public async Task Diagnoses_an_anomaly_that_has_none_and_saves_the_evidence_with_the_explanation()
    {
        var anomaly = Anomaly();
        _store.Pending.Add(anomaly);

        await Create().RunAsync(T0.AddMinutes(7), CancellationToken.None);

        var saved = Assert.Single(_store.Saved);
        Assert.Equal(anomaly.AnomalyId, saved.AnomalyId);
        Assert.Equal(T0.AddMinutes(7), saved.DiagnosedAt);
        Assert.Equal("spend.volume", saved.Evidence.TopMover!.Id);
        Assert.Equal(NarrationSource.Template, saved.Narration.Source);
        Assert.Equal(anomaly.Series, saved.Evidence.Series);
    }

    [Fact]
    public async Task Looks_at_the_window_the_detector_judged_and_the_hour_before_it()
    {
        _store.Pending.Add(Anomaly(minutesAfter: 6));

        await Create().RunAsync(T0.AddMinutes(7), CancellationToken.None);

        // Opened at 02:06 with a two-minute lag, so the detector judged the five minutes up to 02:04.
        Assert.Equal(
            [(T0.AddMinutes(-1), T0.AddMinutes(4)), (T0.AddMinutes(-61), T0.AddMinutes(-1))],
            _source.Requests.Select(r => (r.Start, r.End)));
    }

    [Fact]
    public async Task Asks_for_nothing_when_there_is_nothing_to_diagnose()
    {
        await Create().RunAsync(T0, CancellationToken.None);

        Assert.Empty(_source.Requests);
        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task Only_considers_recent_anomalies_and_a_limited_number_per_round()
    {
        _options.MaxAnomalyAge = TimeSpan.FromMinutes(30);
        _options.BatchSize = 3;

        await Create().RunAsync(T0.AddMinutes(60), CancellationToken.None);

        Assert.Equal(T0.AddMinutes(30), _store.LastOpenedSince);
        Assert.Equal(3, _store.LastLimit);
    }

    [Fact]
    public async Task Does_nothing_when_another_instance_is_already_diagnosing()
    {
        _store.Pending.Add(Anomaly());
        _store.LockAvailable = false;

        await Create().RunAsync(T0.AddMinutes(7), CancellationToken.None);

        Assert.Empty(_store.Saved);
        Assert.Empty(_source.Requests);
    }

    [Fact]
    public async Task One_anomaly_that_cannot_be_diagnosed_does_not_stop_the_others()
    {
        var broken = Anomaly(tenant: "broken");
        var fine = Anomaly(tenant: "fine");
        _store.Pending.AddRange([broken, fine]);
        _source.FailFor = broken.Series;

        await Create().RunAsync(T0.AddMinutes(7), CancellationToken.None);

        Assert.Equal(fine.AnomalyId, Assert.Single(_store.Saved).AnomalyId);
    }

    [Fact]
    public async Task Uses_the_llm_when_it_is_enabled_and_counts_it_against_the_monthly_limit()
    {
        _options.NarratorEnabled = true;
        _options.MaxNarrationsPerMonth = 1;
        _llm.Reply = Samples.Reply(ids: ["spend.volume"]);
        _store.Pending.AddRange([Anomaly(tenant: "first"), Anomaly(tenant: "second")]);

        await Create().RunAsync(T0.AddMinutes(7), CancellationToken.None);

        // Only one explanation is allowed this month, so the second is written by the template.
        Assert.Equal(2, _store.Saved.Count);
        Assert.Equal(NarrationSource.Llm, _store.Saved[0].Narration.Source);
        Assert.Equal(NarrationSource.Template, _store.Saved[1].Narration.Source);
        Assert.Contains("monthly limit", _store.Saved[1].Narration.FallbackReason, StringComparison.Ordinal);
        Assert.Equal(1, _llm.Calls);
    }

    [Fact]
    public async Task Counts_the_llm_explanations_already_written_this_month_before_it_starts()
    {
        _options.NarratorEnabled = true;
        _options.MaxNarrationsPerMonth = 5;
        _store.LlmNarrationsThisMonth = 5;
        _llm.Reply = Samples.Reply(ids: ["spend.volume"]);
        _store.Pending.Add(Anomaly());

        await Create().RunAsync(new DateTimeOffset(2026, 3, 18, 9, 30, 0, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), _store.LastCountSince);
        Assert.Equal(0, _llm.Calls);
        Assert.Equal(NarrationSource.Template, Assert.Single(_store.Saved).Narration.Source);
    }

    [Fact]
    public async Task Does_not_count_llm_explanations_when_the_narrator_is_off()
    {
        _store.Pending.Add(Anomaly());

        await Create().RunAsync(T0.AddMinutes(7), CancellationToken.None);

        Assert.Null(_store.LastCountSince);
    }

    [Fact]
    public async Task Still_saves_the_template_when_the_llm_gives_nonsense()
    {
        _options.NarratorEnabled = true;
        _llm.Reply = "I think it is probably the weather.";
        _store.Pending.Add(Anomaly());

        await Create().RunAsync(T0.AddMinutes(7), CancellationToken.None);

        var saved = Assert.Single(_store.Saved);
        Assert.Equal(NarrationSource.Template, saved.Narration.Source);
        Assert.Contains("refused", saved.Narration.FallbackReason, StringComparison.Ordinal);
    }

    private sealed class FakeEvidenceSource : IEvidenceSource
    {
        public List<(SeriesKey Series, DateTimeOffset Start, DateTimeOffset End)> Requests { get; } = [];

        public SeriesKey? FailFor { get; set; }

        // A five-minute request is the anomaly window, and anything longer is the period before it.
        public ValueTask<PeriodTotals> LoadPeriodAsync(SeriesKey series, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken)
        {
            Requests.Add((series, periodStart, periodEnd));

            if (FailFor == series)
            {
                throw new InvalidOperationException("the database is down");
            }

            var minutes = (int)(periodEnd - periodStart).TotalMinutes;
            return ValueTask.FromResult(minutes == 5
                ? Periods.Of(5, new ModelSpec("gpt-4o-mini", 600))
                : Periods.Of(minutes, new ModelSpec("gpt-4o-mini", 100)));
        }
    }

    private sealed class FakeDiagnosisStore : IDiagnosisStore
    {
        public List<AnomalyToDiagnose> Pending { get; } = [];

        public List<DiagnosisRecord> Saved { get; } = [];

        public bool LockAvailable { get; set; } = true;

        public int LlmNarrationsThisMonth { get; set; }

        public DateTimeOffset? LastOpenedSince { get; private set; }

        public int? LastLimit { get; private set; }

        public DateTimeOffset? LastCountSince { get; private set; }

        public async ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            if (!LockAvailable)
            {
                return false;
            }

            await work(cancellationToken);
            return true;
        }

        public ValueTask<IReadOnlyList<AnomalyToDiagnose>> LoadUndiagnosedAsync(DateTimeOffset openedSince, int limit, CancellationToken cancellationToken)
        {
            LastOpenedSince = openedSince;
            LastLimit = limit;
            return ValueTask.FromResult<IReadOnlyList<AnomalyToDiagnose>>([.. Pending.Where(a => !Saved.Any(s => s.AnomalyId == a.AnomalyId)).Take(limit)]);
        }

        public ValueTask<int> CountLlmNarrationsAsync(DateTimeOffset since, CancellationToken cancellationToken)
        {
            LastCountSince = since;
            return ValueTask.FromResult(LlmNarrationsThisMonth);
        }

        public ValueTask SaveAsync(DiagnosisRecord record, CancellationToken cancellationToken)
        {
            Saved.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubLlm : ILlmClient
    {
        public string Reply { get; set; } = string.Empty;

        public int Calls { get; private set; }

        public ValueTask<LlmReply> CompleteJsonAsync(LlmPrompt prompt, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new LlmReply(Reply, "stub-model"));
        }
    }
}
