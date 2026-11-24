using System.Net;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;

namespace Fusebox.Gateway.Tests;

/// <summary>
/// One record per authenticated request, whatever became of it. The model is priced at $0.001 per input token and
/// $0.002 per output token, and the fake vendor reports 10 input and 20 output tokens ($0.05).
/// </summary>
public sealed class GatewayTelemetryTests : IAsyncLifetime
{
    private FakeUpstream _upstream = null!;
    private GatewayFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeUpstream.StartAsync();
        _factory = new GatewayFactory(_upstream, budgetUsd: 1.00m);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private Task<HttpResponseMessage> PostAsync(string? body = null, string? apiKey = GatewayFactory.TenantKey, string path = "/openai/v1/chat/completions") =>
        ChatRequests.PostAsync(_client, body ?? ChatRequests.Body(), apiKey, path);

    private RequestRecord TheOnlyRecord() => Assert.Single(_factory.Telemetry.Records);

    [Fact]
    public async Task Records_a_served_call_with_its_tokens_and_cost()
    {
        await PostAsync();

        var record = TheOnlyRecord();
        Assert.Equal(TenantId.Parse("acme"), record.TenantId);
        Assert.Equal("openai-chat", record.RouteId);
        Assert.Equal(GatewayFactory.Model, record.Model);
        Assert.Equal(RequestDisposition.Forwarded, record.Disposition);
        Assert.Equal(200, record.StatusCode);
        Assert.Equal(new TokenUsage(10, 20), record.Usage);
        Assert.Equal(Money.FromDollars(0.05m), record.Cost);
        Assert.Equal(Money.FromDollars(0.201m), record.Reserved);
        Assert.False(record.IsStreaming);
        Assert.Null(record.DenialReason);
    }

    [Fact]
    public async Task Records_how_long_the_call_took()
    {
        var release = new TaskCompletionSource();
        _upstream.Gate = release.Task;

        var call = PostAsync();
        while (_upstream.Requests.Count == 0)
        {
            await Task.Delay(10);
        }

        _factory.Time.Advance(TimeSpan.FromSeconds(2));
        release.SetResult();
        await call;

        Assert.Equal(TimeSpan.FromSeconds(2), TheOnlyRecord().Duration);
        Assert.Equal(DateTimeOffset.Parse("2026-03-04T10:15:00Z"), TheOnlyRecord().StartedAt);
    }

    [Fact]
    public async Task Records_a_call_the_budget_refused_because_the_tenant_has_spent_it()
    {
        // Each call is held at $0.201 and really costs $0.20, so four fit in $1.00 and the fifth is refused.
        _upstream.ResponseBody = """{"usage":{"prompt_tokens":0,"completion_tokens":100}}""";
        for (var i = 0; i < 4; i++)
        {
            await PostAsync();
        }

        var response = await PostAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var record = _factory.Telemetry.Records.Last();
        Assert.Equal(5, _factory.Telemetry.Records.Count);
        Assert.Equal(RequestDisposition.Denied, record.Disposition);
        Assert.Equal(DenialReason.BudgetExceeded, record.DenialReason);
        Assert.Equal(429, record.StatusCode);
        Assert.Equal(Money.Zero, record.Cost);
    }

    [Fact]
    public async Task Records_a_call_that_could_never_fit_in_the_budget()
    {
        // 600 output tokens at $0.002 is $1.20, more than the whole $1.00 budget.
        var response = await PostAsync(ChatRequests.Body(maxTokens: 600));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DenialReason.RequestExceedsBudget, TheOnlyRecord().DenialReason);
    }

    [Fact]
    public async Task Records_a_call_for_a_model_with_no_price()
    {
        await PostAsync(ChatRequests.Body(model: "mystery"));

        var record = TheOnlyRecord();
        Assert.Equal(RequestDisposition.Denied, record.Disposition);
        Assert.Equal(DenialReason.UnknownModel, record.DenialReason);
        Assert.Equal("mystery", record.Model);
    }

    [Fact]
    public async Task Records_a_request_that_was_not_valid()
    {
        await PostAsync("not json");

        var record = TheOnlyRecord();
        Assert.Equal(RequestDisposition.Rejected, record.Disposition);
        Assert.Equal(400, record.StatusCode);
        Assert.Null(record.Model);
    }

    [Fact]
    public async Task Records_a_request_that_was_too_large()
    {
        await PostAsync(new string(' ', 10_000));

        Assert.Equal(RequestDisposition.Rejected, TheOnlyRecord().Disposition);
        Assert.Equal(413, TheOnlyRecord().StatusCode);
    }

    [Fact]
    public async Task Records_a_vendor_error_as_forwarded_with_its_status_and_no_cost()
    {
        _upstream.StatusCode = StatusCodes.Status500InternalServerError;
        _upstream.ResponseBody = """{"error":{"message":"vendor outage"}}""";

        await PostAsync();

        var record = TheOnlyRecord();
        Assert.Equal(RequestDisposition.Forwarded, record.Disposition);
        Assert.Equal(500, record.StatusCode);
        Assert.Equal(Money.Zero, record.Cost);
        Assert.Null(record.Usage);
    }

    [Fact]
    public async Task Records_a_streamed_call_with_the_usage_read_from_the_stream()
    {
        _upstream.ContentType = "text/event-stream";
        _upstream.ResponseBody = FakeUpstream.StreamWithUsage;

        var response = await PostAsync(ChatRequests.Body(stream: true));
        await response.Content.ReadAsStringAsync();

        var record = TheOnlyRecord();
        Assert.True(record.IsStreaming);
        Assert.Equal(new TokenUsage(10, 20), record.Usage);
        Assert.Equal(Money.FromDollars(0.05m), record.Cost);
    }

    [Fact]
    public async Task Records_a_call_charged_at_its_reservation_with_no_usage_known()
    {
        _upstream.ResponseBody = """{"id":"no-usage-here"}""";

        await PostAsync();

        var record = TheOnlyRecord();
        Assert.Null(record.Usage);
        Assert.Equal(Money.FromDollars(0.201m), record.Cost);
    }

    [Fact]
    public async Task Records_a_call_on_a_route_that_is_not_metered()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/openai/v1/models");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GatewayFactory.TenantKey);

        await _client.SendAsync(request);

        var record = TheOnlyRecord();
        Assert.Equal("openai-models", record.RouteId);
        Assert.Equal(RequestDisposition.Forwarded, record.Disposition);
        Assert.Null(record.Model);
        Assert.Equal(Money.Zero, record.Cost);
    }

    [Fact]
    public async Task Does_not_record_a_call_that_never_identified_a_tenant()
    {
        await PostAsync(apiKey: null);

        Assert.Empty(_factory.Telemetry.Records);
    }

    [Fact]
    public async Task A_sink_that_refuses_records_changes_nothing_for_the_caller()
    {
        _factory.Telemetry.Accepting = false;

        var response = await PostAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeUpstream.CompletionWithUsage, await response.Content.ReadAsStringAsync());
        Assert.Empty(_factory.Telemetry.Records);
    }
}
