using System.Globalization;
using Fusebox.Domain;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Catalog;

/// <summary>Rejects an invalid catalog at startup and on reload, so bad config never reaches a request.</summary>
internal sealed class CatalogOptionsValidator : IValidateOptions<CatalogOptions>
{
    private const int Sha256HexLength = 64;

    public ValidateOptionsResult Validate(string? name, CatalogOptions options)
    {
        var failures = new List<string>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (id, tenant) in options.Tenants)
        {
            ValidateTenant(id, tenant, seenHashes, failures);
        }

        foreach (var (model, price) in options.Models)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                failures.Add("A model price has an empty model name.");
            }

            if (price.InputUsdPerMillionTokens < 0 || price.OutputUsdPerMillionTokens < 0)
            {
                failures.Add($"Model '{model}' has a negative price.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateTenant(string id, TenantOptions tenant, HashSet<string> seenHashes, List<string> failures)
    {
        if (!TenantId.TryParse(id, out _))
        {
            failures.Add($"Tenant id '{id}' must be 1-{TenantId.MaxLength} characters of letters, digits, '-' or '_'.");
        }

        if (string.IsNullOrWhiteSpace(tenant.Name))
        {
            failures.Add($"Tenant '{id}' needs a name.");
        }

        if (tenant.ApiKeyHash.Length != Sha256HexLength || !tenant.ApiKeyHash.All(char.IsAsciiHexDigitLower))
        {
            failures.Add($"Tenant '{id}' needs ApiKeyHash to be a lowercase hex SHA-256 ({Sha256HexLength} characters).");
        }
        else if (!seenHashes.Add(tenant.ApiKeyHash))
        {
            failures.Add($"Tenant '{id}' shares an API key hash with another tenant.");
        }

        if (!PriceMultiplier.TryToBasisPoints(tenant.PriceMultiplier, out _))
        {
            failures.Add($"Tenant '{id}' needs a PriceMultiplier between 0.0001 and 100.");
        }

        foreach (var (model, price) in tenant.ModelPrices)
        {
            if (string.IsNullOrWhiteSpace(model) || price.InputUsdPerMillionTokens < 0 || price.OutputUsdPerMillionTokens < 0)
            {
                failures.Add($"Tenant '{id}' has a negotiated price for '{model}' that is empty or negative.");
            }
        }

        if (tenant.BudgetLimitUsd <= 0)
        {
            failures.Add($"Tenant '{id}' needs a BudgetLimitUsd above zero.");
        }

        if (tenant.BudgetWindow < BudgetPolicy.MinimumWindow || tenant.BudgetWindow.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Tenant '{id}' needs a BudgetWindow of at least {BudgetPolicy.MinimumWindow} in whole seconds."));
        }
    }
}
