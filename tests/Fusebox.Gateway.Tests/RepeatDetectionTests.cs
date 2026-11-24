using Fusebox.Application.Telemetry;

namespace Fusebox.Gateway.Tests;

/// <summary>A call identical to one a moment ago is marked as a repeat, the signature of a retry loop.</summary>
public sealed class RepeatDetectionTests : IAsyncLifetime
{
    private FakeUpstream _upstream = null!;
    private GatewayFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeUpstream.StartAsync();
        _factory = new GatewayFactory(_upstream, budgetUsd: 100m);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private async Task<HttpResponseMessage> CallAsync(string? body = null, string? retryCount = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(body ?? ChatRequests.Body(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GatewayFactory.TenantKey);

        if (retryCount is not null)
        {
            request.Headers.Add("X-Stainless-Retry-Count", retryCount);
        }

        return await _client.SendAsync(request);
    }

    private IReadOnlyList<bool> Repeats() => [.. _factory.Telemetry.Records.Select(r => r.IsRepeat)];

    [Fact]
    public async Task The_same_call_sent_again_is_a_repeat_and_the_first_is_not()
    {
        using var first = await CallAsync();
        using var second = await CallAsync();
        using var third = await CallAsync();

        Assert.Equal([false, true, true], Repeats());
    }

    [Fact]
    public async Task A_different_call_is_not_a_repeat()
    {
        using var first = await CallAsync(ChatRequests.Body());
        using var second = await CallAsync(ChatRequests.Body().Replace("\"model\"", "\"temperature\":0.5,\"model\"", StringComparison.Ordinal));

        Assert.Equal([false, false], Repeats());
    }

    [Fact]
    public async Task A_client_that_declares_a_retry_is_marked_even_if_the_call_is_new()
    {
        using var response = await CallAsync(retryCount: "2");

        Assert.Equal([true], Repeats());
    }

    [Fact]
    public async Task A_retry_count_of_zero_is_the_first_attempt()
    {
        using var response = await CallAsync(retryCount: "0");

        Assert.Equal([false], Repeats());
    }

    [Fact]
    public async Task Repeats_are_not_marked_when_the_feature_is_off()
    {
        await using var factory = new GatewayFactory(_upstream, budgetUsd: 100m, new Dictionary<string, string?> { ["Fusebox:RepeatDetection:Enabled"] = "false" });
        using var client = factory.CreateClient();

        using var first = await ChatRequests.PostAsync(client, ChatRequests.Body(), GatewayFactory.TenantKey, "/openai/v1/chat/completions");
        using var second = await ChatRequests.PostAsync(client, ChatRequests.Body(), GatewayFactory.TenantKey, "/openai/v1/chat/completions");

        Assert.All(factory.Telemetry.Records, r => Assert.False(r.IsRepeat));
    }
}
