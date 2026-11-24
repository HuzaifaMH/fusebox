using System.Globalization;

namespace Fusebox.Domain;

/// <summary>
/// A non-negative amount of money, stored as whole micro-dollars (1 USD = 1,000,000).
/// Integer arithmetic keeps budget counters exact; floating point would drift.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public const long MicroDollarsPerDollar = 1_000_000;

    private Money(long microDollars) => MicroDollars = microDollars;

    public static Money Zero => default;

    public long MicroDollars { get; }

    public static Money FromMicroDollars(long microDollars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microDollars);
        return new Money(microDollars);
    }

    /// <summary>Converts dollars to micro-dollars, rounding up so cost is never under-counted.</summary>
    public static Money FromDollars(decimal dollars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dollars);
        return new Money(checked((long)decimal.Ceiling(dollars * MicroDollarsPerDollar)));
    }

    public decimal ToDollars() => (decimal)MicroDollars / MicroDollarsPerDollar;

    public Money Add(Money other) => new(checked(MicroDollars + other.MicroDollars));

    /// <exception cref="InvalidOperationException">The result would be negative.</exception>
    public Money Subtract(Money other)
    {
        if (other.MicroDollars > MicroDollars)
        {
            throw new InvalidOperationException("Money cannot be negative.");
        }

        return new Money(MicroDollars - other.MicroDollars);
    }

    /// <summary>Subtracts <paramref name="other"/>, flooring at zero.</summary>
    public Money SubtractOrZero(Money other) =>
        other.MicroDollars >= MicroDollars ? Zero : new Money(MicroDollars - other.MicroDollars);

    public int CompareTo(Money other) => MicroDollars.CompareTo(other.MicroDollars);

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"${ToDollars():0.000000}");
}
