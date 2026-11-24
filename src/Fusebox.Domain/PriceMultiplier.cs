namespace Fusebox.Domain;

/// <summary>Converts the multiplier people write (0.85) to the basis points Fusebox stores (8,500), without losing digits silently.</summary>
public static class PriceMultiplier
{
    /// <summary>Whether <paramref name="multiplier"/> is a usable multiplier: 0.0001 to 100, with at most four decimal places.</summary>
    public static bool TryToBasisPoints(decimal multiplier, out int basisPoints)
    {
        basisPoints = 0;
        var scaled = multiplier * TenantPricing.ListBasisPoints;

        if (scaled != decimal.Truncate(scaled) || scaled < TenantPricing.MinBasisPoints || scaled > TenantPricing.MaxBasisPoints)
        {
            return false;
        }

        basisPoints = (int)scaled;
        return true;
    }

    /// <exception cref="ArgumentOutOfRangeException">Not a usable multiplier. See <see cref="TryToBasisPoints"/>.</exception>
    public static int ToBasisPoints(decimal multiplier) =>
        TryToBasisPoints(multiplier, out var basisPoints)
            ? basisPoints
            : throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "The price multiplier must be between 0.0001 and 100, with at most four decimal places.");

    public static decimal FromBasisPoints(int basisPoints) => (decimal)basisPoints / TenantPricing.ListBasisPoints;
}
