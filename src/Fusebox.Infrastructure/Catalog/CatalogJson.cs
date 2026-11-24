using System.Security.Cryptography;
using System.Text.Json;
using Fusebox.Domain;

namespace Fusebox.Infrastructure.Catalog;

/// <summary>
/// The catalog as JSON, for storing in Redis and for telling two catalogs apart.
/// The output is canonical (sorted, no whitespace), so equal catalogs always give equal JSON and equal hashes.
/// </summary>
internal static class CatalogJson
{
    public static byte[] Serialize(CatalogData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var dto = new CatalogDto(
            [.. data.Tenants
                .OrderBy(t => t.Tenant.Id.Value, StringComparer.Ordinal)
                .Select(t => new TenantDto(
                    t.Tenant.Id.Value,
                    t.Tenant.Name,
                    t.ApiKeyHash,
                    t.Tenant.Budget.Limit.MicroDollars,
                    (long)t.Tenant.Budget.Window.TotalSeconds,
                    t.Tenant.Pricing.MultiplierBasisPoints,
                    [.. t.Tenant.Pricing.ModelPrices
                        .OrderBy(m => m.Key, StringComparer.Ordinal)
                        .Select(m => new PriceDto(m.Key, m.Value.InputPerMillionTokens.MicroDollars, m.Value.OutputPerMillionTokens.MicroDollars))]))],
            [.. data.Prices
                .OrderBy(p => p.Model, StringComparer.Ordinal)
                .Select(p => new PriceDto(
                    p.Model,
                    p.Price.InputPerMillionTokens.MicroDollars,
                    p.Price.OutputPerMillionTokens.MicroDollars))]);

        return JsonSerializer.SerializeToUtf8Bytes(dto);
    }

    /// <summary>A hash that is the same for equal catalogs, so a change can be detected without comparing them.</summary>
    public static string Hash(CatalogData data) => Convert.ToHexStringLower(SHA256.HashData(Serialize(data)));

    /// <summary>Reads a catalog written by <see cref="Serialize"/>. Returns false if the JSON is not a valid catalog.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> json, out CatalogData? data)
    {
        data = null;

        try
        {
            var dto = JsonSerializer.Deserialize<CatalogDto>(json);
            if (dto?.Tenants is null || dto.Prices is null)
            {
                return false;
            }

            data = new CatalogData(
                [.. dto.Tenants.Select(t => new TenantEntry(
                    t.ApiKeyHash,
                    new Tenant(
                        TenantId.Parse(t.Id),
                        t.Name,
                        new BudgetPolicy(Money.FromMicroDollars(t.BudgetLimitMicroUsd), TimeSpan.FromSeconds(t.BudgetWindowSeconds)))
                    {
                        Pricing = new TenantPricing(
                            t.PriceMultiplierBasisPoints,
                            (t.ModelPrices ?? []).ToDictionary(
                                m => m.Model,
                                m => new ModelPrice(Money.FromMicroDollars(m.InputMicroUsdPerMillion), Money.FromMicroDollars(m.OutputMicroUsdPerMillion)),
                                StringComparer.Ordinal)),
                    }))],
                [.. dto.Prices.Select(p => new PriceEntry(
                    p.Model,
                    new ModelPrice(
                        Money.FromMicroDollars(p.InputMicroUsdPerMillion),
                        Money.FromMicroDollars(p.OutputMicroUsdPerMillion))))]);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException or OverflowException)
        {
            // Includes values the domain types reject, such as a negative amount or a window under a minute.
            return false;
        }
    }

    // The pricing fields have defaults, so a catalog that another gateway version wrote to Redis before they existed still reads as list price.
    private sealed record TenantDto(
        string Id,
        string Name,
        string ApiKeyHash,
        long BudgetLimitMicroUsd,
        long BudgetWindowSeconds,
        int PriceMultiplierBasisPoints = TenantPricing.ListBasisPoints,
        List<PriceDto>? ModelPrices = null);

    private sealed record PriceDto(string Model, long InputMicroUsdPerMillion, long OutputMicroUsdPerMillion);

    private sealed record CatalogDto(List<TenantDto> Tenants, List<PriceDto> Prices);
}
