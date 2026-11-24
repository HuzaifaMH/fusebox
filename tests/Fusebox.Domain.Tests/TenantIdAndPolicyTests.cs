namespace Fusebox.Domain.Tests;

public class TenantIdTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("Team-42_prod")]
    [InlineData("a")]
    public void Valid_ids_are_accepted(string value)
    {
        Assert.True(TenantId.TryParse(value, out var id));
        Assert.Equal(value, id!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("brace{injection}")]
    [InlineData("colon:separated")]
    [InlineData("ünïcode")]
    public void Invalid_ids_are_rejected(string value)
    {
        Assert.False(TenantId.TryParse(value, out _));
        Assert.Throws<ArgumentException>(() => TenantId.Parse(value));
    }

    [Fact]
    public void Null_and_overlong_ids_are_rejected()
    {
        Assert.False(TenantId.TryParse(null, out _));
        Assert.False(TenantId.TryParse(new string('a', TenantId.MaxLength + 1), out _));
        Assert.True(TenantId.TryParse(new string('a', TenantId.MaxLength), out _));
    }

    [Fact]
    public void Ids_with_the_same_value_are_equal()
    {
        Assert.Equal(TenantId.Parse("acme"), TenantId.Parse("acme"));
    }
}

public class BudgetPolicyTests
{
    [Fact]
    public void A_valid_policy_keeps_its_values()
    {
        var policy = new BudgetPolicy(Money.FromDollars(50m), TimeSpan.FromHours(1));

        Assert.Equal(Money.FromDollars(50m), policy.Limit);
        Assert.Equal(TimeSpan.FromHours(1), policy.Window);
    }

    [Fact]
    public void Windows_shorter_than_a_minute_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetPolicy(Money.Zero, TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void Windows_must_be_whole_seconds()
    {
        Assert.Throws<ArgumentException>(() => new BudgetPolicy(Money.Zero, TimeSpan.FromMilliseconds(60_500)));
    }
}
