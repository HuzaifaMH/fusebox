namespace Fusebox.Domain.Tests;

public class BudgetWindowTests
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    [Fact]
    public void Hourly_window_is_aligned_to_the_hour()
    {
        var window = BudgetWindow.Containing(DateTimeOffset.Parse("2026-03-04T10:42:17Z"), OneHour);

        Assert.Equal(DateTimeOffset.Parse("2026-03-04T10:00:00Z"), window.Start);
        Assert.Equal(DateTimeOffset.Parse("2026-03-04T11:00:00Z"), window.End);
    }

    [Fact]
    public void An_instant_exactly_on_a_boundary_belongs_to_the_window_that_starts_there()
    {
        var window = BudgetWindow.Containing(DateTimeOffset.Parse("2026-03-04T11:00:00Z"), OneHour);

        Assert.Equal(DateTimeOffset.Parse("2026-03-04T11:00:00Z"), window.Start);
    }

    [Fact]
    public void Instants_in_the_same_window_share_a_start()
    {
        var first = BudgetWindow.Containing(DateTimeOffset.Parse("2026-03-04T10:00:00Z"), OneHour);
        var last = BudgetWindow.Containing(DateTimeOffset.Parse("2026-03-04T10:59:59Z"), OneHour);

        Assert.Equal(first, last);
    }

    [Fact]
    public void RemainingAt_counts_down_to_the_end_and_floors_at_zero()
    {
        var window = BudgetWindow.Containing(DateTimeOffset.Parse("2026-03-04T10:30:00Z"), OneHour);

        Assert.Equal(TimeSpan.FromMinutes(30), window.RemainingAt(DateTimeOffset.Parse("2026-03-04T10:30:00Z")));
        Assert.Equal(TimeSpan.Zero, window.RemainingAt(DateTimeOffset.Parse("2026-03-04T12:00:00Z")));
    }

    [Fact]
    public void Window_size_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BudgetWindow.Containing(DateTimeOffset.UnixEpoch, TimeSpan.Zero));
    }
}
