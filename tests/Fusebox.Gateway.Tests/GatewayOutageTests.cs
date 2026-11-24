using System.Net;
using Fusebox.Domain;

namespace Fusebox.Gateway.Tests;

/// <summary>
/// The gateway with its real budget-store wiring pointed at a Redis that is not there.
/// The tenant has a $1.00 budget, so one gateway instance may spend $0.25 on its own while degraded.
/// </summary>
public sealed class GatewayOutageTests : IAsyncLifetime
{
    private static readonly Dictionary<string, string?> DeadRedis = new()
    {
        ["Fusebox:Enforcement:Store"] = "Redis",
        ["Fusebox:Enforcement:Redis:ConnectionString"] = "127.0.0.1:1,connectTimeout=200,abortConnect=false",
    };

    private FakeUpstream _upstream = null!;
    private GatewayFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeUpstream.StartAsync();
        _factory = new GatewayFactory(_upstream, budgetUsd: 1.00m, DeadRedis);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private Task<HttpResponseMessage> PostAsync(string path = "/openai/v1/chat/completions") =>
        ChatRequests.PostAsync(_client, ChatRequests.Body(), path: path);

    [Fact]
    public async Task Keeps_serving_on_a_local_limit_when_the_budget_store_is_down()
    {
        var response = await PostAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_upstream.Requests);
    }

    [Fact]
    public async Task Stops_a_tenant_at_its_local_share_of_the_budget()
    {
        // Each call really costs $0.20 and is held at $0.201. The second one would pass $0.25.
        _upstream.ResponseBody = """{"usage":{"prompt_tokens":0,"completion_tokens":100}}""";

        var first = await PostAsync();
        var second = await PostAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Single(_upstream.Requests);
    }

    [Fact]
    public async Task Rejects_a_fail_closed_route_with_503_and_never_calls_the_vendor()
    {
        var response = await PostAsync("/strict/v1/chat/completions");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("urn:fusebox:problem:enforcement-unavailable", (await ChatRequests.ReadProblemAsync(response)).GetProperty("type").GetString());
        Assert.Empty(_upstream.Requests);
    }
}
