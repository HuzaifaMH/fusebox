using System.Net;
using Fusebox.Application.Telemetry;
using Fusebox.Domain;

namespace Fusebox.Gateway.Tests;

/// <summary>A vendor that keeps failing is cut off, and is let back in once it answers a test call.</summary>
public sealed class UpstreamBreakerTests : IAsyncLifetime
{
    private static readonly Dictionary<string, string?> ThreeFailures = new()
    {
        ["Fusebox:Upstream:Breaker:FailureThreshold"] = "3",
        ["Fusebox:Upstream:Breaker:OpenFor"] = "00:00:30",
    };

    private FakeUpstream _upstream = null!;
    private GatewayFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _upstream.DisposeAsync();
    }

    private async Task StartAsync(Dictionary<string, string?>? settings = null, decimal budgetUsd = 100m)
    {
        _upstream = await FakeUpstream.StartAsync();
        _factory = new GatewayFactory(_upstream, budgetUsd, settings ?? ThreeFailures);
        _client = _factory.CreateClient();
    }

    private Task<HttpResponseMessage> CallAsync() =>
        ChatRequests.PostAsync(_client, ChatRequests.Body(), GatewayFactory.TenantKey, "/openai/v1/chat/completions");

    private async Task FailTimesAsync(int times)
    {
        _upstream.StatusCode = StatusCodes.Status503ServiceUnavailable;

        for (var i = 0; i < times; i++)
        {
            using var response = await CallAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
    }

    [Fact]
    public async Task After_enough_failures_calls_are_refused_at_once_and_never_reach_the_vendor()
    {
        await StartAsync();
        await FailTimesAsync(3);
        var reachedVendor = _upstream.Requests.Count;

        using var refused = await CallAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("upstream-unavailable", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("30", refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(reachedVendor, _upstream.Requests.Count);
    }

    [Fact]
    public async Task A_refused_call_holds_no_budget_and_is_recorded_as_unavailable()
    {
        await StartAsync();
        await FailTimesAsync(3);

        using var refused = await CallAsync();

        var record = _factory.Telemetry.Records.Last();
        Assert.Equal(RequestDisposition.Unavailable, record.Disposition);
        Assert.Equal(Money.Zero, record.Reserved);
        Assert.Equal(Money.Zero, record.Cost);
    }

    [Fact]
    public async Task Fewer_failures_than_the_threshold_leave_the_vendor_connected()
    {
        await StartAsync();
        await FailTimesAsync(2);
        _upstream.StatusCode = StatusCodes.Status200OK;

        using var response = await CallAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task After_the_pause_a_test_call_goes_through_and_a_healthy_answer_reconnects_the_vendor()
    {
        await StartAsync();
        await FailTimesAsync(3);
        _upstream.StatusCode = StatusCodes.Status200OK;

        using var stillCutOff = await CallAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stillCutOff.StatusCode);

        _factory.Time.Advance(TimeSpan.FromSeconds(31));

        using var test = await CallAsync();
        using var after = await CallAsync();

        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_failed_test_call_cuts_the_vendor_off_for_another_pause()
    {
        await StartAsync();
        await FailTimesAsync(3);
        _factory.Time.Advance(TimeSpan.FromSeconds(31));

        using var test = await CallAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, test.StatusCode);
        var reachedVendor = _upstream.Requests.Count;

        using var refused = await CallAsync();

        Assert.Contains("upstream-unavailable", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(reachedVendor, _upstream.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    public async Task A_vendor_that_refuses_calls_is_not_cut_off_because_it_is_still_answering(int status)
    {
        await StartAsync();
        _upstream.StatusCode = status;

        for (var i = 0; i < 6; i++)
        {
            using var response = await CallAsync();
            Assert.Equal(status, (int)response.StatusCode);
        }

        Assert.Equal(6, _upstream.Requests.Count);
    }

    [Fact]
    public async Task Fusebox_refusing_calls_for_budget_does_not_count_against_the_vendor()
    {
        // A budget of one cent is below the worst case of a single call, so every call is refused before the vendor sees it.
        await StartAsync(budgetUsd: 0.01m);

        for (var i = 0; i < 6; i++)
        {
            using var response = await CallAsync();
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("upstream-unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Empty(_upstream.Requests);
    }

    [Fact]
    public async Task With_the_breakers_off_every_call_reaches_the_vendor()
    {
        await StartAsync(new() { ["Fusebox:Upstream:Breaker:Enabled"] = "false", ["Fusebox:Upstream:Breaker:FailureThreshold"] = "1" });
        _upstream.StatusCode = StatusCodes.Status503ServiceUnavailable;

        for (var i = 0; i < 5; i++)
        {
            using var response = await CallAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        Assert.Equal(5, _upstream.Requests.Count);
    }

    [Fact]
    public async Task A_failing_vendor_does_not_stop_a_route_that_goes_to_another_vendor()
    {
        await StartAsync(new(ThreeFailures)
        {
            ["ReverseProxy:Clusters:other:Destinations:primary:Address"] = "http://127.0.0.1:1/",
            ["ReverseProxy:Routes:other:ClusterId"] = "other",
            ["ReverseProxy:Routes:other:Match:Path"] = "/other/v1/chat/completions",
            ["ReverseProxy:Routes:other:Transforms:0:PathRemovePrefix"] = "/other",
            ["ReverseProxy:Routes:other:Metadata:Fusebox.Protocol"] = "openai-chat",
        });
        await FailTimesAsync(3);
        _upstream.StatusCode = StatusCodes.Status200OK;

        // The openai vendor is cut off; the other one is judged separately, so its call is attempted (and fails for its own reason).
        using var other = await ChatRequests.PostAsync(_client, ChatRequests.Body(), GatewayFactory.TenantKey, "/other/v1/chat/completions");

        Assert.DoesNotContain("upstream-unavailable", await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
