namespace Fusebox.Domain;

/// <summary>What a model costs per one million input and output tokens.</summary>
public sealed record ModelPrice
{
    private const long TokensPerUnit = 1_000_000;

    public ModelPrice(Money inputPerMillionTokens, Money outputPerMillionTokens)
    {
        InputPerMillionTokens = inputPerMillionTokens;
        OutputPerMillionTokens = outputPerMillionTokens;
    }

    /// <summary>
    /// This price times <paramref name="basisPoints"/> ten-thousandths (8,500 is 85%), each rate rounded up to the next
    /// micro-dollar so the result is never below the true figure.
    /// </summary>
    public ModelPrice Scaled(int basisPoints)
    {
        if (basisPoints == TenantPricing.ListBasisPoints)
        {
            return this;
        }

        return new ModelPrice(ScaleUp(InputPerMillionTokens, basisPoints), ScaleUp(OutputPerMillionTokens, basisPoints));
    }

    private static Money ScaleUp(Money rate, int basisPoints)
    {
        var scaled = ((Int128)rate.MicroDollars * basisPoints) + TenantPricing.ListBasisPoints - 1;
        return Money.FromMicroDollars(checked((long)(scaled / TenantPricing.ListBasisPoints)));
    }

    public Money InputPerMillionTokens { get; }

    public Money OutputPerMillionTokens { get; }

    /// <summary>
    /// The cost of <paramref name="usage"/>, rounded up to the next micro-dollar once for the whole call,
    /// so cost is never under-counted and rounding error does not accumulate per token kind.
    /// </summary>
    public Money CostOf(TokenUsage usage)
    {
        var microDollarTokens =
            ((Int128)usage.InputTokens * InputPerMillionTokens.MicroDollars) +
            ((Int128)usage.OutputTokens * OutputPerMillionTokens.MicroDollars);

        var microDollars = (microDollarTokens + TokensPerUnit - 1) / TokensPerUnit;

        return Money.FromMicroDollars(checked((long)microDollars));
    }
}
