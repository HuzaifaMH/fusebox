namespace Fusebox.Domain;

/// <summary>
/// What a tenant pays, compared with the vendor's list price: a multiplier on every list price (0.85 for a 15% discount),
/// and negotiated rates for particular models that replace the list price altogether.
/// </summary>
/// <remarks>
/// For a model the tenant has its own rate for, that rate is used as it is. For any other model the list price is scaled by
/// the multiplier. Scaling always rounds up to the next micro-dollar, so a price is never understated.
/// </remarks>
public sealed class TenantPricing : IEquatable<TenantPricing>
{
    /// <summary>Basis points that mean "list price": 10,000 is 1.0.</summary>
    public const int ListBasisPoints = 10_000;

    /// <summary>The smallest and largest multiplier accepted, 0.0001 to 100. Anything outside is a typo.</summary>
    public const int MinBasisPoints = 1;

    public const int MaxBasisPoints = 1_000_000;

    private readonly IReadOnlyDictionary<string, ModelPrice> _modelPrices;

    /// <param name="multiplierBasisPoints">Ten thousandths of the list price: 8,500 is 85%.</param>
    /// <param name="modelPrices">Negotiated rates, keyed by the model name clients send.</param>
    /// <exception cref="ArgumentOutOfRangeException">The multiplier is outside the accepted range.</exception>
    public TenantPricing(int multiplierBasisPoints, IReadOnlyDictionary<string, ModelPrice>? modelPrices = null)
    {
        if (multiplierBasisPoints is < MinBasisPoints or > MaxBasisPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(multiplierBasisPoints),
                multiplierBasisPoints,
                $"The price multiplier must be between {MinBasisPoints} and {MaxBasisPoints} basis points (0.0001 to 100).");
        }

        MultiplierBasisPoints = multiplierBasisPoints;
        _modelPrices = modelPrices is { Count: > 0 }
            ? new Dictionary<string, ModelPrice>(modelPrices, StringComparer.Ordinal)
            : EmptyPrices;
    }

    private static readonly IReadOnlyDictionary<string, ModelPrice> EmptyPrices = new Dictionary<string, ModelPrice>();

    /// <summary>Pays list price.</summary>
    public static TenantPricing List { get; } = new(ListBasisPoints);

    public int MultiplierBasisPoints { get; }

    public IReadOnlyDictionary<string, ModelPrice> ModelPrices => _modelPrices;

    /// <summary>What this tenant pays for <paramref name="model"/>, given the vendor's list price (or null if it has none).</summary>
    /// <returns>The price, or null if neither the tenant nor the vendor has one, so the model cannot be priced.</returns>
    public ModelPrice? PriceOf(string model, ModelPrice? listPrice)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (_modelPrices.TryGetValue(model, out var negotiated))
        {
            return negotiated;
        }

        return listPrice?.Scaled(MultiplierBasisPoints);
    }

    public bool Equals(TenantPricing? other) =>
        other is not null
        && MultiplierBasisPoints == other.MultiplierBasisPoints
        && _modelPrices.Count == other._modelPrices.Count
        && _modelPrices.All(pair => other._modelPrices.TryGetValue(pair.Key, out var price) && price == pair.Value);

    public override bool Equals(object? obj) => Equals(obj as TenantPricing);

    public override int GetHashCode() => HashCode.Combine(MultiplierBasisPoints, _modelPrices.Count);
}
