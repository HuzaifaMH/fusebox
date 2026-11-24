namespace Fusebox.Domain.Tests;

public class MoneyTests
{
    [Fact]
    public void FromDollars_converts_to_micro_dollars()
    {
        Assert.Equal(1_500_000, Money.FromDollars(1.5m).MicroDollars);
    }

    [Fact]
    public void FromDollars_rounds_up_so_cost_is_never_under_counted()
    {
        Assert.Equal(1, Money.FromDollars(0.0000001m).MicroDollars);
    }

    [Fact]
    public void Negative_amounts_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.FromMicroDollars(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.FromDollars(-0.01m));
    }

    [Fact]
    public void Default_is_zero()
    {
        Assert.Equal(Money.Zero, default);
        Assert.Equal(0, Money.Zero.MicroDollars);
    }

    [Fact]
    public void Add_and_subtract_are_exact()
    {
        var total = Money.FromMicroDollars(300) + Money.FromMicroDollars(200);

        Assert.Equal(500, total.MicroDollars);
        Assert.Equal(200, (total - Money.FromMicroDollars(300)).MicroDollars);
    }

    [Fact]
    public void Subtract_below_zero_throws()
    {
        Assert.Throws<InvalidOperationException>(() => Money.FromMicroDollars(1) - Money.FromMicroDollars(2));
    }

    [Fact]
    public void SubtractOrZero_floors_at_zero()
    {
        Assert.Equal(Money.Zero, Money.FromMicroDollars(1).SubtractOrZero(Money.FromMicroDollars(2)));
        Assert.Equal(1, Money.FromMicroDollars(3).SubtractOrZero(Money.FromMicroDollars(2)).MicroDollars);
    }

    [Fact]
    public void Amounts_compare_by_value()
    {
        var small = Money.FromMicroDollars(1);
        var large = Money.FromMicroDollars(2);

        Assert.True(small < large);
        Assert.True(large > small);
        Assert.True(small <= Money.FromMicroDollars(1));
        Assert.True(large >= Money.FromMicroDollars(2));
        Assert.Equal(Money.FromMicroDollars(1), small);
    }

    [Fact]
    public void ToString_uses_invariant_dollars()
    {
        Assert.Equal("$1.250000", Money.FromDollars(1.25m).ToString());
    }
}
