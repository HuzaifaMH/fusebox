using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Enforcement;

public enum EnforcementStoreKind
{
    /// <summary>A single-process ledger. For local development and tests only: instances do not share spend.</summary>
    InMemory,

    /// <summary>A shared Redis ledger. Required whenever more than one gateway instance runs.</summary>
    Redis,
}

public sealed class EnforcementOptions
{
    public const string SectionName = "Fusebox:Enforcement";

    public EnforcementStoreKind Store { get; set; } = EnforcementStoreKind.InMemory;

    /// <summary>
    /// How long past the end of its window a reservation can still be settled.
    /// It must exceed the longest call a client may keep open.
    /// </summary>
    public TimeSpan SettlementGrace { get; set; } = TimeSpan.FromMinutes(10);

    public RedisOptions Redis { get; set; } = new();
}

public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Prefix for every key Fusebox writes, so it can share a Redis instance.</summary>
    public string KeyPrefix { get; set; } = "fusebox";

    /// <summary>How long a Redis command may take before it fails. Short, so a slow Redis cannot stall requests.</summary>
    public int CommandTimeoutMilliseconds { get; set; } = 200;
}

internal sealed class EnforcementOptionsValidator : IValidateOptions<EnforcementOptions>
{
    public ValidateOptionsResult Validate(string? name, EnforcementOptions options)
    {
        var failures = new List<string>();

        if (options.SettlementGrace < TimeSpan.FromMinutes(1))
        {
            failures.Add("SettlementGrace must be at least one minute.");
        }

        if (options.Store == EnforcementStoreKind.Redis)
        {
            if (string.IsNullOrWhiteSpace(options.Redis.ConnectionString))
            {
                failures.Add("Redis.ConnectionString is required when Store is Redis.");
            }

            if (string.IsNullOrWhiteSpace(options.Redis.KeyPrefix) || options.Redis.KeyPrefix.Contains('{', StringComparison.Ordinal))
            {
                failures.Add("Redis.KeyPrefix must be non-empty and must not contain '{'.");
            }

            if (options.Redis.CommandTimeoutMilliseconds is < 10 or > 10_000)
            {
                failures.Add("Redis.CommandTimeoutMilliseconds must be between 10 and 10000.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
