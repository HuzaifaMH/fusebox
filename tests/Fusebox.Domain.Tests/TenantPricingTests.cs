namespace Fusebox.Domain.Tests;

public class TenantPricingTests
{
    private static readonly ModelPrice List = new(Money.FromDollars(10m), Money.FromDollars(30m));

    [Fact]
    public void List_pricing_leaves_the_price_alone()
    {
        Assert.Same(List, TenantPricing.List.PriceOf("m", List));
    }

    [Fact]
    public void A_multiplier_scales_both_rates()
    {
        var pricing = new TenantPricing(8_500);

        var price = pricing.PriceOf("m", List);

        Assert.Equal(Money.FromDollars(8.5m), price!.InputPerMillionTokens);
        Assert.Equal(Money.FromDollars(25.5m), price.OutputPerMillionTokens);
    }

    [Fact]
    public void Scaling_rounds_up_so_a_price_is_never_understated()
    {
        // 1 micro-dollar at 33.33% is a third of a micro-dollar, which must become 1, not 0.
        var scaled = new ModelPrice(Money.FromMicroDollars(1), Money.FromMicroDollars(3)).Scaled(3_333);

        Assert.Equal(Money.FromMicroDollars(1), scaled.InputPerMillionTokens);
        Assert.Equal(Money.FromMicroDollars(1), scaled.OutputPerMillionTokens);
    }

    [Fact]
    public void A_rate_negotiated_for_a_model_replaces_the_list_price_and_the_multiplier()
    {
        var negotiated = new ModelPrice(Money.FromDollars(4m), Money.FromDollars(9m));
        var pricing = new TenantPricing(8_500, new Dictionary<string, ModelPrice> { ["m"] = negotiated });

        Assert.Equal(negotiated, pricing.PriceOf("m", List));
        Assert.Equal(Money.FromDollars(8.5m), pricing.PriceOf("other", List)!.InputPerMillionTokens);
    }

    [Fact]
    public void A_negotiated_rate_prices_a_model_the_vendor_does_not_list()
    {
        var negotiated = new ModelPrice(Money.FromDollars(4m), Money.FromDollars(9m));
        var pricing = new TenantPricing(10_000, new Dictionary<string, ModelPrice> { ["private-model"] = negotiated });

        Assert.Equal(negotiated, pricing.PriceOf("private-model", listPrice: null));
        Assert.Null(pricing.PriceOf("unknown", listPrice: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void A_multiplier_outside_the_accepted_range_is_refused(int basisPoints)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TenantPricing(basisPoints));
    }

    [Fact]
    public void Equal_terms_are_equal()
    {
        var rate = new ModelPrice(Money.FromDollars(1m), Money.FromDollars(2m));

        Assert.Equal(
            new TenantPricing(9_000, new Dictionary<string, ModelPrice> { ["m"] = rate }),
            new TenantPricing(9_000, new Dictionary<string, ModelPrice> { ["m"] = rate }));
        Assert.NotEqual(new TenantPricing(9_000), new TenantPricing(9_001));
    }

    [Fact]
    public void A_tenant_pays_list_price_unless_told_otherwise()
    {
        var tenant = new Tenant(TenantId.Parse("acme"), "Acme", new BudgetPolicy(Money.FromDollars(1m), TimeSpan.FromHours(1)));

        Assert.Equal(TenantPricing.List, tenant.Pricing);
    }

    [Theory]
    [InlineData("1", 10_000)]
    [InlineData("0.85", 8_500)]
    [InlineData("0.0001", 1)]
    [InlineData("100", 1_000_000)]
    [InlineData("1.2345", 12_345)]
    public void A_multiplier_converts_to_basis_points_exactly(string text, int expected)
    {
        Assert.True(PriceMultiplier.TryToBasisPoints(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), out var basisPoints));
        Assert.Equal(expected, basisPoints);
        Assert.Equal(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), PriceMultiplier.FromBasisPoints(basisPoints));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("100.0001")]
    [InlineData("0.12345")]
    public void A_multiplier_that_is_out_of_range_or_would_lose_digits_is_refused(string text)
    {
        Assert.False(PriceMultiplier.TryToBasisPoints(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), out _));
    }
}
