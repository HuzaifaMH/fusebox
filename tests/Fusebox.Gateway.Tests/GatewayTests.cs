using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fusebox.Application.Budgeting;
using Fusebox.Domain;

namespace Fusebox.Gateway.Tests;

/// <summary>
/// End-to-end tests of the gateway: real HTTP into the real pipeline, real YARP proxying to a fake vendor.
/// Each test gets its own gateway, upstream and budget, with a priced model where a token costs
/// $0.001 (input) or $0.002 (output).
/// </summary>
public sealed class GatewayTests : IAsyncLifetime
{
    private FakeUpstream _upstream = null!;
    private GatewayFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync() => await StartAsync(budgetUsd: 1.00m);

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    /// <summary>Restarts the gateway with a different tenant budget.</summary>
    private async Task StartAsync(decimal budgetUsd)
    {
        if (_factory is not null)
        {
            await DisposeAsync();
        }

        _upstream = await FakeUpstream.StartAsync();
        _factory = new GatewayFactory(_upstream, budgetUsd);
        _client = _factory.CreateClient();
    }

    // A tiny prompt (about one input token) with max_tokens 100 reserves roughly $0.201.
    private static string Chat(string model = GatewayFactory.Model, int maxTokens = 100, bool stream = false) =>
        ChatRequests.Body(model, maxTokens, stream);

    private Task<HttpResponseMessage> PostChatAsync(string body, string? apiKey = GatewayFactory.TenantKey, string path = "/openai/v1/chat/completions") =>
        ChatRequests.PostAsync(_client, body, apiKey, path);

    /// <summary>The tenant's spend so far, read by reserving nothing.</summary>
    private async Task<Money> SpentAsync()
    {
        var store = _factory.Services.GetRequiredService<IEnforcementStore>();
        var policy = new BudgetPolicy(Money.FromDollars(1_000_000m), TimeSpan.FromHours(1));
        var charge = new BudgetCharge(TenantId.Parse("acme"), Guid.NewGuid(), Money.Zero, policy, _factory.Time.GetUtcNow());

        return (await store.TryReserveAsync(charge, CancellationToken.None)).Spent;
    }

    private static Task<JsonElement> ReadProblemAsync(HttpResponseMessage response) =>
        ChatRequests.ReadProblemAsync(response);

    [Fact]
    public async Task Rejects_a_request_with_no_api_key_and_never_calls_the_vendor()
    {
        var response = await PostChatAsync(Chat(), apiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        Assert.Equal("urn:fusebox:problem:unauthorized", (await ReadProblemAsync(response)).GetProperty("type").GetString());
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Rejects_an_unknown_api_key_and_never_calls_the_vendor()
    {
        var response = await PostChatAsync(Chat(), apiKey: "not-a-real-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Forwards_an_allowed_request_and_returns_the_vendor_response_unchanged()
    {
        var response = await PostChatAsync(Chat());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FakeUpstream.CompletionWithUsage, await response.Content.ReadAsStringAsync());
        Assert.Single(_upstream.Requests);
    }

    [Fact]
    public async Task Swaps_the_tenant_key_for_the_vendor_key_so_the_tenant_key_never_reaches_the_vendor()
    {
        await PostChatAsync(Chat());

        var received = Assert.Single(_upstream.Requests);
        Assert.Equal($"Bearer {GatewayFactory.VendorKey}", received.Authorization);
        Assert.DoesNotContain(GatewayFactory.TenantKey, received.Authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwards_the_request_body_to_the_vendor_intact()
    {
        var body = Chat();

        await PostChatAsync(body);

        Assert.Equal(body, Assert.Single(_upstream.Requests).Body);
        Assert.Equal("/v1/chat/completions", _upstream.Requests.Single().Path);
    }

    [Fact]
    public async Task Asks_the_vendor_for_an_uncompressed_response_so_usage_can_be_read()
    {
        await PostChatAsync(Chat());

        Assert.True(string.IsNullOrEmpty(Assert.Single(_upstream.Requests).AcceptEncoding));
    }

    [Fact]
    public async Task Charges_the_real_cost_after_the_call_not_the_worst_case_reserved_before_it()
    {
        await PostChatAsync(Chat());

        // The vendor reported 10 input tokens ($0.01) and 20 output tokens ($0.04).
        Assert.Equal(Money.FromDollars(0.05m), await SpentAsync());
    }

    [Fact]
    public async Task Blocks_a_request_when_the_budget_is_used_up_without_calling_the_vendor()
    {
        // A $0.30 budget fits one $0.201 reservation. The first call settles at $0.05, so spend a
        // lot more with an expensive call to leave too little room for the next.
        await StartAsync(budgetUsd: 0.30m);
        _upstream.ResponseBody = """{"usage":{"prompt_tokens":0,"completion_tokens":100}}""";
        await PostChatAsync(Chat());
        Assert.Equal(Money.FromDollars(0.20m), await SpentAsync());
        var callsBefore = _upstream.Requests.Count;

        var response = await PostChatAsync(Chat());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(callsBefore, _upstream.Requests.Count);

        var problem = await ReadProblemAsync(response);
        Assert.Equal("urn:fusebox:problem:budget-exceeded", problem.GetProperty("type").GetString());
        Assert.Equal(0.10m, problem.GetProperty("remainingUsd").GetDecimal());
    }

    [Fact]
    public async Task A_blocked_request_says_how_long_to_wait()
    {
        await StartAsync(budgetUsd: 0.30m);
        _upstream.ResponseBody = """{"usage":{"prompt_tokens":0,"completion_tokens":100}}""";
        await PostChatAsync(Chat());

        var response = await PostChatAsync(Chat());

        // It is 10:15 and the hourly window ends at 11:00.
        Assert.Equal(TimeSpan.FromMinutes(45), response.Headers.RetryAfter!.Delta);
    }

    [Fact]
    public async Task Budget_resets_in_the_next_window()
    {
        await StartAsync(budgetUsd: 0.30m);
        _upstream.ResponseBody = """{"usage":{"prompt_tokens":0,"completion_tokens":100}}""";
        await PostChatAsync(Chat());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostChatAsync(Chat())).StatusCode);

        _factory.Time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(HttpStatusCode.OK, (await PostChatAsync(Chat())).StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_collectively_exceed_the_budget()
    {
        // A $1.00 budget fits four $0.201 reservations but not five. Hold every call in flight at the vendor
        // so no reservation is settled, then fire many at once: exactly four may get through.
        var release = new TaskCompletionSource();
        _upstream.Gate = release.Task;

        var calls = Enumerable.Range(0, 12).Select(_ => PostChatAsync(Chat())).ToList();

        // The eight blocked calls return immediately, while the four allowed calls wait at the vendor.
        var blocked = new List<HttpResponseMessage>();
        while (blocked.Count < 8)
        {
            var finished = await Task.WhenAny(calls);
            calls.Remove(finished);
            blocked.Add(await finished);
        }

        Assert.All(blocked, response => Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode));
        Assert.Equal(4, _upstream.Requests.Count);

        release.SetResult();
        var allowed = await Task.WhenAll(calls);
        Assert.All(allowed, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
    }

    [Fact]
    public async Task Refuses_a_request_that_could_cost_more_than_the_whole_budget()
    {
        // 1,000,000 output tokens at $0.002 would cost $2,000 against a $1.00 budget.
        var response = await PostChatAsync(Chat(maxTokens: 1_000_000));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("urn:fusebox:problem:request-exceeds-budget", (await ReadProblemAsync(response)).GetProperty("type").GetString());
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Refuses_a_model_that_has_no_price()
    {
        var response = await PostChatAsync(Chat(model: "unpriced-model"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("urn:fusebox:problem:unknown-model", (await ReadProblemAsync(response)).GetProperty("type").GetString());
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Rejects_a_body_that_is_not_a_valid_chat_request()
    {
        var response = await PostChatAsync("this is not json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task Rejects_an_oversized_body()
    {
        var response = await PostChatAsync(JsonSerializer.Serialize(new
        {
            model = GatewayFactory.Model,
            messages = new[] { new { role = "user", content = new string('x', 10_000) } },
        }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task A_vendor_error_is_passed_through_and_is_not_charged()
    {
        _upstream.StatusCode = StatusCodes.Status500InternalServerError;
        _upstream.ResponseBody = """{"error":{"message":"vendor outage"}}""";

        var response = await PostChatAsync(Chat());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("vendor outage", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(Money.Zero, await SpentAsync());
    }

    [Fact]
    public async Task A_vendor_response_without_usage_is_charged_at_the_reservation()
    {
        _upstream.ResponseBody = """{"id":"no-usage-here"}""";

        await PostChatAsync(Chat());

        // 1 input token ($0.001) plus the 100 output token cap ($0.20).
        Assert.Equal(Money.FromDollars(0.201m), await SpentAsync());
    }

    [Fact]
    public async Task A_streamed_response_is_settled_at_the_usage_the_vendor_reports()
    {
        _upstream.ContentType = "text/event-stream";
        _upstream.ResponseBody = FakeUpstream.StreamWithUsage;

        var response = await PostChatAsync(Chat(stream: true));
        await response.Content.ReadAsStringAsync();

        // The vendor reported 10 input tokens ($0.01) and 20 output tokens ($0.04).
        Assert.Equal(Money.FromDollars(0.05m), await SpentAsync());
    }

    [Fact]
    public async Task Asks_the_vendor_to_report_usage_when_the_caller_streams_without_asking()
    {
        _upstream.ContentType = "text/event-stream";
        _upstream.ResponseBody = FakeUpstream.StreamWithUsage;

        var response = await PostChatAsync(Chat(stream: true));
        await response.Content.ReadAsStringAsync();

        using var sent = JsonDocument.Parse(Assert.Single(_upstream.Requests).Body);
        Assert.True(sent.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(GatewayFactory.Model, sent.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Hides_the_usage_event_from_a_caller_who_did_not_ask_for_it()
    {
        _upstream.ContentType = "text/event-stream";
        _upstream.ResponseBody = FakeUpstream.StreamWithUsage;

        var response = await PostChatAsync(Chat(stream: true));
        var received = await response.Content.ReadAsStringAsync();

        Assert.Equal(FakeUpstream.StreamWithoutUsage, received);
    }

    [Fact]
    public async Task Passes_the_usage_event_to_a_caller_who_asked_for_it()
    {
        _upstream.ContentType = "text/event-stream";
        _upstream.ResponseBody = FakeUpstream.StreamWithUsage;
        var body = """{"model":"test-model","max_tokens":100,"stream":true,"stream_options":{"include_usage":true},"messages":[{"role":"user","content":"hi"}]}""";

        var response = await PostChatAsync(body);
        var received = await response.Content.ReadAsStringAsync();

        Assert.Equal(FakeUpstream.StreamWithUsage, received);
        Assert.Equal(Money.FromDollars(0.05m), await SpentAsync());
    }

    [Fact]
    public async Task A_stream_that_ends_without_usage_is_charged_at_the_reservation()
    {
        _upstream.ContentType = "text/event-stream";
        _upstream.ResponseBody = FakeUpstream.StreamWithoutUsage;

        var response = await PostChatAsync(Chat(stream: true));
        await response.Content.ReadAsStringAsync();

        // 1 input token ($0.001) plus the 100 output token cap ($0.20).
        Assert.Equal(Money.FromDollars(0.201m), await SpentAsync());
    }

    [Fact]
    public async Task A_fail_closed_route_is_served_normally_while_the_budget_store_is_healthy()
    {
        var response = await PostChatAsync(Chat(), path: "/strict/v1/chat/completions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_upstream.Requests);
    }

    [Fact]
    public async Task A_route_with_an_unknown_failure_mode_is_not_forwarded()
    {
        var response = await PostChatAsync(Chat(), path: "/badmode/v1/chat/completions");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("urn:fusebox:problem:route-misconfigured", (await ReadProblemAsync(response)).GetProperty("type").GetString());
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task A_route_marked_unmetered_passes_through_without_touching_the_budget()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/openai/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GatewayFactory.TenantKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Money.Zero, await SpentAsync());
    }

    [Fact]
    public async Task An_unmetered_route_still_requires_an_api_key()
    {
        var response = await _client.GetAsync(new Uri("/openai/v1/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task A_route_with_no_metering_decision_is_refused_rather_than_silently_unmetered()
    {
        var response = await PostChatAsync(Chat(), path: "/undecided/v1/chat/completions");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("urn:fusebox:problem:route-misconfigured", (await ReadProblemAsync(response)).GetProperty("type").GetString());
        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task The_health_endpoint_is_open_and_reports_healthy()
    {
        var response = await _client.GetAsync(new Uri("/healthz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
