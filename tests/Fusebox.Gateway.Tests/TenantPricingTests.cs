using Fusebox.Application.Telemetry;
using Fusebox.Domain;

namespace Fusebox.Gateway.Tests;

/// <summary>
/// A tenant's own pricing, through the real gateway. At list price the fake vendor's 10 input and 20 output tokens cost
/// $0.05 and the worst case held beforehand is $0.201, so half price must give exactly half of each.
/// </summary>
public sealed class TenantPricingTests : IAsyncLifetime
{
    private FakeUpstream _upstream = null!;

    public async Task InitializeAsync() => _upstream = await FakeUpstream.StartAsync();

    public async Task DisposeAsync() => await _upstream.DisposeAsync();

    private async Task<RequestRecord> CallAsync(Dictionary<string, string?>? settings)
    {
        await using var factory = new GatewayFactory(_upstream, budgetUsd: 1.00m, settings);
        using var client = factory.CreateClient();

        using var response = await ChatRequests.PostAsync(client, ChatRequests.Body(), GatewayFactory.TenantKey, "/openai/v1/chat/completions");
        Assert.True(response.IsSuccessStatusCode);

        return Assert.Single(factory.Telemetry.Records);
    }

    [Fact]
    public async Task A_tenant_on_list_terms_is_charged_list_price()
    {
        var record = await CallAsync(settings: null);

        Assert.Equal(Money.FromDollars(0.05m), record.Cost);
    }

    [Fact]
    public async Task A_discount_lowers_both_what_is_held_and_what_is_charged()
    {
        var record = await CallAsync(new() { ["Fusebox:Catalog:Tenants:acme:PriceMultiplier"] = "0.5" });

        Assert.Equal(Money.FromDollars(0.025m), record.Cost);
        Assert.Equal(Money.FromDollars(0.1005m), record.Reserved);
    }

    [Fact]
    public async Task A_negotiated_rate_replaces_the_list_price()
    {
        var record = await CallAsync(new()
        {
            ["Fusebox:Catalog:Tenants:acme:ModelPrices:test-model:InputUsdPerMillionTokens"] = "100",
            ["Fusebox:Catalog:Tenants:acme:ModelPrices:test-model:OutputUsdPerMillionTokens"] = "200",
        });

        // 10 input tokens at $100 per million plus 20 output tokens at $200 per million.
        Assert.Equal(Money.FromDollars(0.005m), record.Cost);
    }
}
