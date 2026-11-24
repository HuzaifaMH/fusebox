using Fusebox.Infrastructure.Catalog;
using Microsoft.Extensions.Options;

namespace Fusebox.Gateway.Admin;

/// <summary>The admin API, for managing tenants, budgets, keys and prices and for reading anomalies and alerts. Section <c>Fusebox:Admin</c>.</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Fusebox:Admin";

    /// <summary>Off by default. When off, the admin routes do not exist.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The lowercase hex SHA-256 of the admin key. Callers send the key itself in the <c>X-Admin-Key</c> header.
    /// Make the hash with: <c>printf %s "$KEY" | sha256sum</c>. Supply it through an environment variable, not a file.
    /// </summary>
    public string ApiKeyHash { get; set; } = string.Empty;
}

internal sealed class AdminOptionsValidator(IOptions<CatalogSourceOptions> catalog) : IValidateOptions<AdminOptions>
{
    private const int Sha256HexLength = 64;

    public ValidateOptionsResult Validate(string? name, AdminOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (options.ApiKeyHash.Length != Sha256HexLength || !options.ApiKeyHash.All(char.IsAsciiHexDigitLower))
        {
            failures.Add($"Admin needs ApiKeyHash to be a lowercase hex SHA-256 ({Sha256HexLength} characters).");
        }

        if (catalog.Value.Kind != CatalogSourceKind.Postgres)
        {
            failures.Add("The admin API changes tenants and prices in PostgreSQL, so it needs Fusebox:CatalogSource:Kind to be Postgres.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
