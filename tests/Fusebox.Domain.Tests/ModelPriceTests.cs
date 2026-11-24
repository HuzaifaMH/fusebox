namespace Fusebox.Domain.Tests;

public class ModelPriceTests
{
    // $2.50 per million input tokens and $10.00 per million output tokens.
    private static readonly ModelPrice Price = new(Money.FromDollars(2.50m), Money.FromDollars(10m));

    [Fact]
    public void CostOf_prices_input_and_output_separately()
    {
        var cost = Price.CostOf(new TokenUsage(inputTokens: 1_000_000, outputTokens: 1_000_000));

        Assert.Equal(Money.FromDollars(12.50m), cost);
    }

    [Fact]
    public void CostOf_zero_usage_is_free()
    {
        Assert.Equal(Money.Zero, Price.CostOf(default));
    }

    [Fact]
    public void CostOf_rounds_up_to_the_next_micro_dollar()
    {
        // 1 input token costs 2.5 micro-dollars, which rounds up to 3.
        Assert.Equal(3, Price.CostOf(new TokenUsage(1, 0)).MicroDollars);
    }

    [Fact]
    public void CostOf_rounds_once_for_the_whole_call_rather_than_per_token_kind()
    {
        // One token of each kind costs 0.4 micro-dollars, so the call costs 0.8, which rounds up to 1.
        // Rounding each kind separately would charge 2 and overstate the cost.
        var cheap = new ModelPrice(Money.FromMicroDollars(400_000), Money.FromMicroDollars(400_000));

        Assert.Equal(1, cheap.CostOf(new TokenUsage(1, 1)).MicroDollars);
    }

    [Fact]
    public void CostOf_handles_very_large_usage_without_overflow()
    {
        var cost = Price.CostOf(new TokenUsage(10_000_000_000, 10_000_000_000));

        Assert.Equal(Money.FromDollars(125_000m), cost);
    }

    [Fact]
    public void TokenUsage_rejects_negative_counts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenUsage(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenUsage(0, -1));
    }
}
