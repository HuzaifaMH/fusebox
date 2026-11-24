using System.Net;
using System.Text;
using System.Text.Json;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Infrastructure.Diagnosis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Infrastructure.Tests;

public class DiagnosisOptionsValidatorTests
{
    private static LlmOptions ValidLlm() => new() { BaseUrl = "https://api.example.com/", Model = "small-model", ApiKey = "secret" };

    private static DiagnosisOptionsValidator Validator(bool detectionEnabled = true, LlmOptions? llm = null) =>
        new(Options.Create(new DetectionOptions { Enabled = detectionEnabled }), Options.Create(llm ?? new LlmOptions()));

    [Fact]
    public void The_defaults_are_valid_even_when_enabled()
    {
        Assert.True(Validator().Validate(null, new DiagnosisOptions { Enabled = true }).Succeeded);
    }

    [Fact]
    public void Diagnosis_that_is_switched_off_needs_nothing_else()
    {
        Assert.True(Validator(detectionEnabled: false).Validate(null, new DiagnosisOptions()).Succeeded);
    }

    [Fact]
    public void Diagnosis_needs_anomaly_detection_because_that_is_what_it_explains()
    {
        var result = Validator(detectionEnabled: false).Validate(null, new DiagnosisOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains("Detection", string.Join(' ', result.Failures!), StringComparison.Ordinal);
    }

    [Fact]
    public void The_llm_narrator_needs_an_address_a_model_and_a_key()
    {
        var options = new DiagnosisOptions { Enabled = true, NarratorEnabled = true };

        Assert.True(Validator().Validate(null, options).Failed);
        Assert.True(Validator(llm: new LlmOptions { BaseUrl = "https://x/", Model = "m" }).Validate(null, options).Failed);
        Assert.True(Validator(llm: new LlmOptions { BaseUrl = "not a url", Model = "m", ApiKey = "k" }).Validate(null, options).Failed);
        Assert.True(Validator(llm: ValidLlm()).Validate(null, options).Succeeded);
    }

    [Fact]
    public void The_llm_settings_are_ignored_while_the_narrator_is_off()
    {
        Assert.True(Validator().Validate(null, new DiagnosisOptions { Enabled = true, NarratorEnabled = false }).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public void The_output_cap_must_be_sensible(int tokens)
    {
        var llm = ValidLlm();
        llm.MaxOutputTokens = tokens;

        Assert.True(Validator(llm: llm).Validate(null, new DiagnosisOptions { Enabled = true, NarratorEnabled = true }).Failed);
    }

    [Fact]
    public void A_timeout_must_be_between_one_second_and_two_minutes()
    {
        var options = new DiagnosisOptions { Enabled = true, NarratorEnabled = true, NarratorTimeout = TimeSpan.FromMinutes(5) };

        Assert.True(Validator(llm: ValidLlm()).Validate(null, options).Failed);
    }

    [Fact]
    public void Basic_numeric_settings_are_checked()
    {
        Assert.True(Validator().Validate(null, new DiagnosisOptions { Enabled = true, BatchSize = 0 }).Failed);
        Assert.True(Validator().Validate(null, new DiagnosisOptions { Enabled = true, MaxSegments = 50 }).Failed);
        Assert.True(Validator().Validate(null, new DiagnosisOptions { Enabled = true, BeforeMinutes = 1 }).Failed);
    }
}

public class OpenAiCompatibleLlmClientTests
{
    private readonly StubHandler _handler = new();
    private readonly LlmOptions _llm = new() { BaseUrl = "https://llm.example.com", Model = "small-model", ApiKey = "sk-test", MaxOutputTokens = 300 };

    private OpenAiCompatibleLlmClient Create() => new(Options.Create(_llm), new HttpClient(_handler));

    private static readonly LlmPrompt Prompt = new("You are helpful.", """{"evidence":[]}""");

    [Fact]
    public async Task Sends_the_prompt_to_the_chat_completions_endpoint_with_the_key_and_asks_for_json()
    {
        _handler.Respond("""{"model":"small-model-2026","choices":[{"message":{"content":"{\"ok\":true}"}}]}""");

        var reply = await Create().CompleteJsonAsync(Prompt, CancellationToken.None);

        Assert.Equal("{\"ok\":true}", reply.Text);
        Assert.Equal("small-model-2026", reply.Model);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://llm.example.com/v1/chat/completions", request.Uri);
        Assert.Equal("Bearer sk-test", request.Authorization);

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("small-model", root.GetProperty("model").GetString());
        Assert.Equal(0, root.GetProperty("temperature").GetInt32());
        Assert.Equal(300, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(Prompt.System, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal(Prompt.User, messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Sends_nothing_but_the_prompt()
    {
        _handler.Respond("""{"choices":[{"message":{"content":"{}"}}]}""");

        await Create().CompleteJsonAsync(Prompt, CancellationToken.None);

        var body = Assert.Single(_handler.Requests).Body;
        Assert.Equal(["max_tokens", "messages", "model", "response_format", "temperature"], JsonDocument.Parse(body).RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("https://llm.example.com", "v1/chat/completions", "https://llm.example.com/v1/chat/completions")]
    [InlineData("https://llm.example.com/", "/v1/chat/completions", "https://llm.example.com/v1/chat/completions")]
    [InlineData("http://localhost:5071/openai", "v1/chat/completions", "http://localhost:5071/openai/v1/chat/completions")]
    public async Task Builds_the_address_from_the_base_and_the_path_whatever_slashes_they_have(string baseUrl, string path, string expected)
    {
        _llm.BaseUrl = baseUrl;
        _llm.Path = path;
        _handler.Respond("""{"choices":[{"message":{"content":"{}"}}]}""");

        await Create().CompleteJsonAsync(Prompt, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(_handler.Requests).Uri);
    }

    [Fact]
    public async Task Names_the_configured_model_when_the_answer_does_not()
    {
        _handler.Respond("""{"choices":[{"message":{"content":"{}"}}]}""");

        var reply = await Create().CompleteJsonAsync(Prompt, CancellationToken.None);

        Assert.Equal("small-model", reply.Model);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Fails_on_an_error_status_without_repeating_the_body_which_may_echo_the_prompt(HttpStatusCode status)
    {
        _handler.Respond("""{"error":{"message":"your prompt was: {\"evidence\":[]}"}}""", status);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(async () => await Create().CompleteJsonAsync(Prompt, CancellationToken.None));

        Assert.Equal(status, failure.StatusCode);
        Assert.DoesNotContain("evidence", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":5}}]}""")]
    public async Task Fails_on_an_answer_that_does_not_have_the_expected_shape(string body)
    {
        _handler.Respond(body);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Create().CompleteJsonAsync(Prompt, CancellationToken.None));
    }

    [Fact]
    public async Task Stops_when_asked_to()
    {
        _handler.Hang = true;
        using var cancellation = new CancellationTokenSource();
        var pending = Create().CompleteJsonAsync(Prompt, cancellation.Token).AsTask();

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed record Seen(HttpMethod Method, string Uri, string? Authorization, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private string _body = "{}";
        private HttpStatusCode _status = HttpStatusCode.OK;

        public List<Seen> Requests { get; } = [];

        public bool Hang { get; set; }

        public void Respond(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}

public class DiagnosisJobTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-04T12:00:00Z");

    private sealed class ExplodingStore : IDiagnosisStore, IEvidenceSource
    {
        public int Attempts { get; private set; }

        public ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            Attempts++;
            throw new InvalidOperationException("the database is down");
        }

        public ValueTask<IReadOnlyList<AnomalyToDiagnose>> LoadUndiagnosedAsync(DateTimeOffset openedSince, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<int> CountLlmNarrationsAsync(DateTimeOffset since, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask SaveAsync(DiagnosisRecord record, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<PeriodTotals> LoadPeriodAsync(SeriesKey series, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static DiagnosisJob CreateJob(ExplodingStore store, bool enabled)
    {
        var options = Options.Create(new DiagnosisOptions { Enabled = enabled });
        var narrator = new NarrationService(options, NullLogger<NarrationService>.Instance);
        var runner = new DiagnosisRunner(store, store, narrator, options, Options.Create(new DetectionOptions()), NullLogger<DiagnosisRunner>.Instance);

        return new DiagnosisJob(runner, options, new FakeTimeProvider(Now), NullLogger<DiagnosisJob>.Instance);
    }

    [Fact]
    public async Task A_failing_round_is_survived_so_the_next_one_can_run()
    {
        var store = new ExplodingStore();
        var job = CreateJob(store, enabled: true);

        await job.TickAsync(CancellationToken.None);
        await job.TickAsync(CancellationToken.None);

        Assert.Equal(2, store.Attempts);
    }

    [Fact]
    public async Task Does_nothing_when_diagnosis_is_switched_off()
    {
        var store = new ExplodingStore();
        var job = CreateJob(store, enabled: false);

        await job.StartAsync(CancellationToken.None);
        await job.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.Attempts);
    }
}
