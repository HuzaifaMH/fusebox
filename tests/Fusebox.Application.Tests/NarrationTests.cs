using System.Text.Json;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Tests;

internal static class Samples
{
    private static readonly EvidenceEngine Engine = new(new DiagnosisOptions());

    /// <summary>Spend up sixfold because calls per minute went from 100 to 600 on the model "gpt-4o-mini".</summary>
    public static Evidence SpendSurge() => Engine.Analyze(
        Traffic.Acme,
        Signal.Spend,
        Periods.Start,
        Periods.Start.AddMinutes(5),
        Periods.Of(5, new ModelSpec("gpt-4o-mini", 600)),
        Periods.Of(60, new ModelSpec("gpt-4o-mini", 100)));

    /// <summary>The same, with so little data that it should not be trusted much.</summary>
    public static Evidence ThinSpendSurge() => Engine.Analyze(
        Traffic.Acme,
        Signal.Spend,
        Periods.Start,
        Periods.Start.AddMinutes(5),
        new PeriodTotals(2, 5, Periods.Of(5, new ModelSpec("gpt-4o-mini", 600)).ByModel),
        Periods.Of(60, new ModelSpec("gpt-4o-mini", 100)));

    public static string Reply(
        string diagnosis = "Spend per minute rose 6x because billable calls per minute went from 100 to 600, which accounts for 100% of the increase.",
        string confidence = "high",
        string[]? ids = null,
        string remediation = "Look for a retry loop or a caller with no rate limit.") =>
        JsonSerializer.Serialize(new
        {
            diagnosis,
            confidence,
            evidenceIds = ids ?? ["spend.volume", "spend.total"],
            remediation,
        });
}

public class NarrationPromptTests
{
    [Fact]
    public void Carries_the_evidence_with_its_ids_and_numbers()
    {
        var prompt = NarrationPrompt.Build(Samples.SpendSurge());

        using var json = JsonDocument.Parse(prompt.User);
        var items = json.RootElement.GetProperty("evidence").EnumerateArray().ToList();

        Assert.Equal("Spend per minute", json.RootElement.GetProperty("signal").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("windowMinutes").GetInt32());
        Assert.Contains(items, item => item.GetProperty("id").GetString() == "spend.volume" && item.GetProperty("shareOfChange").GetDouble() > 0.99);
        Assert.Contains(items, item => item.GetProperty("id").GetString() == "model.gpt-4o-mini");
    }

    [Fact]
    public void Never_carries_the_tenant()
    {
        var prompt = NarrationPrompt.Build(Samples.SpendSurge());

        Assert.DoesNotContain("acme", prompt.User, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acme", prompt.System, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rounds_numbers_to_four_significant_figures()
    {
        Assert.Equal(0.3333, NarrationPrompt.Round(1.0 / 3));
        Assert.Equal(12_350, NarrationPrompt.Round(12_345.678));
        Assert.Equal(0.0001235, NarrationPrompt.Round(0.00012345678));
    }

    [Fact]
    public void Tells_the_model_to_use_only_the_evidence_and_to_answer_in_a_fixed_shape()
    {
        var system = NarrationPrompt.Build(Samples.SpendSurge()).System;

        Assert.Contains("Use only that evidence", system, StringComparison.Ordinal);
        Assert.Contains("evidenceIds", system, StringComparison.Ordinal);
        Assert.Contains("do not invent numbers", system, StringComparison.Ordinal);
    }
}

public class NarrationValidatorTests
{
    private readonly Evidence _evidence = Samples.SpendSurge();

    private NarrationCheck Validate(string reply, Evidence? evidence = null) =>
        NarrationValidator.Validate(reply, "test-model", evidence ?? _evidence);

    [Fact]
    public void Accepts_an_answer_that_is_well_formed_cites_real_evidence_and_quotes_real_numbers()
    {
        var check = Validate(Samples.Reply());

        Assert.True(check.IsValid, check.Problem);
        Assert.Equal(NarrationSource.Llm, check.Narration!.Source);
        Assert.Equal("test-model", check.Narration.Model);
        Assert.Equal(["spend.volume", "spend.total"], check.Narration.EvidenceIds);
        Assert.Equal(NarrationConfidence.High, check.Narration.Confidence);
    }

    [Fact]
    public void Accepts_an_answer_wrapped_in_a_code_fence()
    {
        var check = Validate("```json\n" + Samples.Reply() + "\n```");

        Assert.True(check.IsValid, check.Problem);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("")]
    public void Refuses_a_reply_that_is_not_a_json_object(string reply)
    {
        Assert.False(Validate(reply).IsValid);
    }

    [Fact]
    public void Refuses_a_reply_with_a_missing_or_extra_field()
    {
        var missing = """{"diagnosis":"x","confidence":"low","evidenceIds":["spend.total"]}""";
        var extra = Samples.Reply().TrimEnd('}') + ",\"notes\":\"anything\"}";

        Assert.False(Validate(missing).IsValid);
        Assert.False(Validate(extra).IsValid);
    }

    [Fact]
    public void Refuses_a_field_of_the_wrong_type()
    {
        var reply = """{"diagnosis":5,"confidence":"low","evidenceIds":["spend.total"],"remediation":"x"}""";

        Assert.False(Validate(reply).IsValid);
        Assert.False(Validate("""{"diagnosis":"x","confidence":"low","evidenceIds":"spend.total","remediation":"x"}""").IsValid);
    }

    [Fact]
    public void Refuses_a_claim_that_cites_evidence_which_was_not_provided()
    {
        var check = Validate(Samples.Reply(ids: ["spend.volume", "spend.retryStorm"]));

        Assert.False(check.IsValid);
        Assert.Contains("spend.retryStorm", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_an_answer_that_cites_no_evidence_or_the_same_evidence_twice()
    {
        Assert.False(Validate(Samples.Reply(ids: [])).IsValid);
        Assert.False(Validate(Samples.Reply(ids: ["spend.volume", "spend.volume"])).IsValid);
    }

    [Fact]
    public void Refuses_a_number_that_is_not_in_the_evidence()
    {
        var check = Validate(Samples.Reply(diagnosis: "Spend rose 6x because calls per minute went from 100 to 600, costing an extra $4,200."));

        Assert.False(check.IsValid);
        Assert.Contains("4,200", check.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Spend rose about 6 times.")]
    [InlineData("Billable calls per minute went from 100 to 600.")]
    [InlineData("Calls per minute account for 100% of the increase.")]
    [InlineData("Spend went up by 500%.")]
    [InlineData("Over the last 5 minutes compared with the previous 60 minutes, spend rose.")]
    [InlineData("The 95th percentile is unaffected.")]
    public void Accepts_numbers_that_are_in_the_evidence_however_they_are_quoted(string diagnosis)
    {
        Assert.True(Validate(Samples.Reply(diagnosis: diagnosis)).IsValid);
    }

    [Theory]
    [InlineData("Spend rose 9x.")]
    [InlineData("Spend rose 40x.")]
    [InlineData("That is about $4.2k extra.")]
    [InlineData("It took 1,200ms.")]
    [InlineData("Calls per minute went from 100 to 900.")]
    [InlineData("This accounts for 40% of the increase.")]
    public void Refuses_numbers_that_are_close_to_nothing_in_the_evidence(string diagnosis)
    {
        Assert.False(Validate(Samples.Reply(diagnosis: diagnosis)).IsValid);
    }

    [Fact]
    public void Does_not_mistake_the_digits_in_a_model_name_for_a_claim_about_the_data()
    {
        var check = Validate(Samples.Reply(diagnosis: "Calls to gpt-4o-mini went from 100 to 600 per minute, a 6x rise."));

        Assert.True(check.IsValid, check.Problem);
    }

    [Fact]
    public void Checks_the_remediation_for_invented_numbers_too()
    {
        var check = Validate(Samples.Reply(remediation: "Set a limit of 37 calls per minute."));

        Assert.False(check.IsValid);
    }

    [Theory]
    [InlineData("certain")]
    [InlineData("")]
    public void Refuses_a_confidence_that_is_not_low_medium_or_high(string confidence)
    {
        Assert.False(Validate(Samples.Reply(confidence: confidence)).IsValid);
    }

    [Fact]
    public void Never_allows_more_confidence_than_the_evidence_supports()
    {
        var thin = Samples.ThinSpendSurge();
        Assert.True(thin.IsThin);

        var check = NarrationValidator.Validate(Samples.Reply(confidence: "high"), "m", thin);

        Assert.True(check.IsValid, check.Problem);
        Assert.Equal(NarrationConfidence.Low, check.Narration!.Confidence);
    }

    [Fact]
    public void Lets_the_model_be_more_cautious_than_the_evidence()
    {
        var check = Validate(Samples.Reply(confidence: "low"));

        Assert.Equal(NarrationConfidence.Low, check.Narration!.Confidence);
    }

    [Fact]
    public void Refuses_an_answer_that_is_empty_or_far_too_long()
    {
        Assert.False(Validate(Samples.Reply(diagnosis: "")).IsValid);
        Assert.False(Validate(Samples.Reply(diagnosis: new string('x', 900))).IsValid);
        Assert.False(Validate(Samples.Reply(remediation: "")).IsValid);
    }
}

public class TemplateNarratorTests
{
    [Fact]
    public void States_the_headline_the_top_factor_and_what_to_do()
    {
        var narration = TemplateNarrator.Narrate(Samples.SpendSurge());

        Assert.Equal(NarrationSource.Template, narration.Source);
        Assert.Contains("Spend per minute rose 6x", narration.Diagnosis, StringComparison.Ordinal);
        Assert.Contains("billable calls per minute", narration.Diagnosis, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100% of the change", narration.Diagnosis, StringComparison.Ordinal);
        Assert.Contains("retry loop", narration.Remediation, StringComparison.Ordinal);
        Assert.Contains("spend.volume", narration.EvidenceIds);
    }

    [Fact]
    public void Names_the_model_that_the_change_came_from()
    {
        var narration = TemplateNarrator.Narrate(Samples.SpendSurge());

        Assert.Contains("gpt-4o-mini", narration.Diagnosis, StringComparison.Ordinal);
    }

    [Fact]
    public void Is_confident_only_when_one_factor_clearly_accounts_for_the_change()
    {
        Assert.Equal(NarrationConfidence.High, TemplateNarrator.Confidence(Samples.SpendSurge()));
        Assert.Equal(NarrationConfidence.Low, TemplateNarrator.Confidence(Samples.ThinSpendSurge()));
    }

    [Fact]
    public void Says_so_when_there_is_little_data()
    {
        var narration = TemplateNarrator.Narrate(Samples.ThinSpendSurge());

        Assert.Contains("little data", narration.Diagnosis, StringComparison.Ordinal);
        Assert.Equal(NarrationConfidence.Low, narration.Confidence);
    }

    [Fact]
    public void Says_so_when_no_cause_could_be_found_instead_of_inventing_one()
    {
        var empty = new EvidenceEngine(new DiagnosisOptions()).Analyze(
            Traffic.Acme, Signal.Spend, Periods.Start, Periods.Start.AddMinutes(5), Periods.Of(5, new ModelSpec("m", 10)), Periods.Of(60, new ModelSpec("m", 0)));

        var narration = TemplateNarrator.Narrate(empty);

        Assert.Contains("could not be traced to one cause", narration.Diagnosis, StringComparison.Ordinal);
        Assert.Equal(NarrationConfidence.Low, narration.Confidence);
    }

    [Fact]
    public void Gives_advice_that_fits_the_cause()
    {
        var engine = new EvidenceEngine(new DiagnosisOptions());
        Evidence Spend(ModelSpec now) => engine.Analyze(Traffic.Acme, Signal.Spend, Periods.Start, Periods.Start.AddMinutes(5), Periods.Of(5, now), Periods.Of(60, new ModelSpec("m", 100)));

        Assert.Contains("prompts or conversation history", TemplateNarrator.Narrate(Spend(new ModelSpec("m", 100, InputTokens: 4_000))).Remediation, StringComparison.Ordinal);
        Assert.Contains("output cap", TemplateNarrator.Narrate(Spend(new ModelSpec("m", 100, OutputTokens: 3_000))).Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void Cites_the_headline_and_the_factors_that_moved_and_not_the_ones_that_did_not()
    {
        var narration = TemplateNarrator.Narrate(Samples.SpendSurge());

        // Input tokens, output tokens and price per token did not move, so they are not cited.
        Assert.Equal(["spend.total", "spend.volume", "model.gpt-4o-mini"], narration.EvidenceIds);
    }

    [Theory]
    [InlineData(0, "$0.00/min")]
    [InlineData(0.004, "$0.0040/min")]
    [InlineData(2, "$2.00/min")]
    [InlineData(1234.5, "$1,234.50/min")]
    public void Writes_dollars_per_minute_plainly(double value, string expected)
    {
        Assert.Equal(expected, EvidenceFormat.Value("$/min", value));
    }

    [Fact]
    public void Records_why_it_stood_in_for_an_llm()
    {
        var narration = TemplateNarrator.Narrate(Samples.SpendSurge(), "the model took too long");

        Assert.Equal("the model took too long", narration.FallbackReason);
    }
}

public class NarrationServiceTests
{
    private readonly FakeLlm _llm = new();
    private readonly DiagnosisOptions _options = new() { NarratorEnabled = true, NarratorTimeout = TimeSpan.FromMilliseconds(300) };
    private readonly Evidence _evidence = Samples.SpendSurge();

    private NarrationService Create(bool withLlm = true) =>
        new(Options.Create(_options), NullLogger<NarrationService>.Instance, withLlm ? _llm : null);

    [Fact]
    public async Task Uses_the_llms_answer_when_it_checks_out()
    {
        _llm.Reply = Samples.Reply();

        var narration = await Create().NarrateAsync(_evidence, allowLlm: true, CancellationToken.None);

        Assert.Equal(NarrationSource.Llm, narration.Source);
        Assert.Equal(1, _llm.Calls);
        Assert.Null(narration.FallbackReason);
    }

    [Fact]
    public async Task Sends_only_the_evidence_prompt()
    {
        _llm.Reply = Samples.Reply();

        await Create().NarrateAsync(_evidence, allowLlm: true, CancellationToken.None);

        Assert.Equal(NarrationPrompt.Build(_evidence), _llm.LastPrompt);
    }

    [Fact]
    public async Task Never_calls_the_llm_when_it_is_switched_off()
    {
        _options.NarratorEnabled = false;

        var narration = await Create().NarrateAsync(_evidence, allowLlm: true, CancellationToken.None);

        Assert.Equal(NarrationSource.Template, narration.Source);
        Assert.Equal(0, _llm.Calls);
        Assert.Null(narration.FallbackReason);
    }

    [Fact]
    public async Task Uses_the_template_when_no_llm_is_configured()
    {
        var narration = await Create(withLlm: false).NarrateAsync(_evidence, allowLlm: true, CancellationToken.None);

        Assert.Equal(NarrationSource.Template, narration.Source);
    }

    [Fact]
    public async Task Does_not_call_the_llm_past_the_monthly_limit_and_says_why()
    {
        var narration = await Create().NarrateAsync(_evidence, allowLlm: false, CancellationToken.None);

        Assert.Equal(NarrationSource.Template, narration.Source);
        Assert.Equal(0, _llm.Calls);
        Assert.Contains("monthly limit", narration.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_template_when_the_answer_invents_a_number()
    {
        _llm.Reply = Samples.Reply(diagnosis: "Spend rose 6x and cost $9,999 extra.");

        var narration = await Create().NarrateAsync(_evidence, allowLlm: true, CancellationToken.None);

        Assert.Equal(NarrationSource.Template, narration.Source);
        Assert.Contains("refused", narration.FallbackReason, StringComparison.Ordinal);
        Assert.DoesNotContain("9,999", narration.Diagnosis, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_template_when_the_llm_fails()
    {
        _llm.Failure = new HttpRequestException("503");

        var narration = await Create().NarrateAsync(_evidence, allowLlm: true, CancellationToken.None);

        Assert.Equal(NarrationSource.Template, narration.Source);
        Assert.Contains("could not be reached", narration.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Falls_back_to_the_template_when_the_llm_is_too_slow()
    {
        _llm.Hang = true;

        var narration = await Create().NarrateAsync(_evidence, allowLlm: true, CancellationToken.None);

        Assert.Equal(NarrationSource.Template, narration.Source);
        Assert.Contains("did not answer within", narration.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lets_a_cancellation_from_the_caller_through_instead_of_hiding_it_behind_the_template()
    {
        _llm.Hang = true;
        using var cancellation = new CancellationTokenSource();
        var pending = Create().NarrateAsync(_evidence, allowLlm: true, cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class FakeLlm : ILlmClient
    {
        public string Reply { get; set; } = string.Empty;

        public Exception? Failure { get; set; }

        public bool Hang { get; set; }

        public int Calls { get; private set; }

        public LlmPrompt? LastPrompt { get; private set; }

        public async ValueTask<LlmReply> CompleteJsonAsync(LlmPrompt prompt, CancellationToken cancellationToken)
        {
            Calls++;
            LastPrompt = prompt;

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Failure is null ? new LlmReply(Reply, "fake-model") : throw Failure;
        }
    }
}
