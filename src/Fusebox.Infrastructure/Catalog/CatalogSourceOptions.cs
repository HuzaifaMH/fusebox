using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Catalog;

public enum CatalogSourceKind
{
    /// <summary>Tenants and prices from the "Fusebox:Catalog" configuration section. For local development and tests.</summary>
    Configuration,

    /// <summary>Tenants and prices from PostgreSQL, the source of truth in production.</summary>
    Postgres,
}

public sealed class CatalogSourceOptions
{
    public const string SectionName = "Fusebox:CatalogSource";

    public CatalogSourceKind Kind { get; set; } = CatalogSourceKind.Configuration;

    /// <summary>How often the catalog is reloaded from its source. Changes made at the source appear within this time.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the gateway checks whether another instance published a newer catalog to Redis, and the pace at
    /// which a failed reload is retried. Short, because the check is a single cheap Redis read.
    /// </summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(15);

    public PostgresOptions Postgres { get; set; } = new();
}

public sealed class PostgresOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Create the tables on startup if they are missing. Turn off where a migration tool owns the schema.</summary>
    public bool ApplySchemaOnStartup { get; set; } = true;

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

internal sealed class CatalogSourceOptionsValidator : IValidateOptions<CatalogSourceOptions>
{
    public ValidateOptionsResult Validate(string? name, CatalogSourceOptions options)
    {
        var failures = new List<string>();

        if (options.RefreshInterval < TimeSpan.FromSeconds(5))
        {
            failures.Add("RefreshInterval must be at least 5 seconds.");
        }

        if (options.SyncInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("SyncInterval must be at least 1 second.");
        }

        if (options.Kind == CatalogSourceKind.Postgres)
        {
            if (string.IsNullOrWhiteSpace(options.Postgres.ConnectionString))
            {
                failures.Add("Postgres.ConnectionString is required when Kind is Postgres.");
            }

            if (options.Postgres.CommandTimeout < TimeSpan.FromSeconds(1))
            {
                failures.Add("Postgres.CommandTimeout must be at least 1 second.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
