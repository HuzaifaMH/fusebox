namespace Fusebox.Domain;

/// <summary>
/// A fixed budget window aligned to the Unix epoch (an hourly window always starts on the hour).
/// Spend is counted per window and starts from zero when the next window opens.
/// </summary>
public readonly record struct BudgetWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public static BudgetWindow Containing(DateTimeOffset instant, TimeSpan size)
    {
        var sizeSeconds = (long)size.TotalSeconds;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeSeconds, nameof(size));

        var seconds = instant.ToUnixTimeSeconds();
        var startSeconds = seconds - (seconds % sizeSeconds);
        var start = DateTimeOffset.FromUnixTimeSeconds(startSeconds);

        return new BudgetWindow(start, start + size);
    }

    public TimeSpan RemainingAt(DateTimeOffset instant) => End > instant ? End - instant : TimeSpan.Zero;
}
