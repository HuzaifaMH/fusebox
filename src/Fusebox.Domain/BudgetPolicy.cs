namespace Fusebox.Domain;

/// <summary>How much a tenant may spend within each fixed time window.</summary>
public sealed record BudgetPolicy
{
    public static readonly TimeSpan MinimumWindow = TimeSpan.FromMinutes(1);

    public BudgetPolicy(Money limit, TimeSpan window)
    {
        if (window < MinimumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, $"The window must be at least {MinimumWindow}.");
        }

        if (window.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException("The window must be a whole number of seconds.", nameof(window));
        }

        Limit = limit;
        Window = window;
    }

    public Money Limit { get; }

    public TimeSpan Window { get; }
}
