using Fusebox.Infrastructure.Catalog;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Telemetry;

public enum TelemetryStoreKind
{
    /// <summary>Requests are not recorded. For local development and tests.</summary>
    None,

    /// <summary>Requests are recorded in PostgreSQL.</summary>
    Postgres,
}

public sealed class TelemetryOptions
{
    public const string SectionName = "Fusebox:Telemetry";

    public TelemetryStoreKind Store { get; set; } = TelemetryStoreKind.None;

    /// <summary>
    /// How many records may wait to be saved. When the queue is full new records are dropped and counted, never
    /// waited for. The default holds about three minutes of traffic at a few dozen requests a second.
    /// </summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>The most records saved in one batch.</summary>
    public int MaxBatchSize { get; set; } = 1_000;

    /// <summary>The longest a record waits for company before its batch is saved anyway.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many times a failing batch is tried, with growing pauses, before it is dropped and counted.</summary>
    public int MaxWriteAttempts { get; set; } = 8;

    /// <summary>How long raw records are kept. Whole days older than this are dropped.</summary>
    public TimeSpan RawRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often the per-minute and per-hour totals are refreshed.</summary>
    public TimeSpan RollupInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How far back each refresh recomputes per-minute totals. A record arriving later than this is missed by them.</summary>
    public TimeSpan MinuteRollupLookback { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How far back each refresh recomputes per-hour totals.</summary>
    public TimeSpan HourRollupLookback { get; set; } = TimeSpan.FromHours(3);

    public PostgresOptions Postgres { get; set; } = new();
}

internal sealed class TelemetryOptionsValidator : IValidateOptions<TelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, TelemetryOptions options)
    {
        var failures = new List<string>();

        if (options.QueueCapacity is < 100 or > 10_000_000)
        {
            failures.Add("QueueCapacity must be between 100 and 10,000,000.");
        }

        if (options.MaxBatchSize is < 1 or > 10_000)
        {
            failures.Add("MaxBatchSize must be between 1 and 10,000.");
        }

        if (options.FlushInterval < TimeSpan.FromMilliseconds(50))
        {
            failures.Add("FlushInterval must be at least 50 milliseconds.");
        }

        if (options.MaxWriteAttempts < 1)
        {
            failures.Add("MaxWriteAttempts must be at least 1.");
        }

        if (options.RawRetention < TimeSpan.FromDays(1))
        {
            failures.Add("RawRetention must be at least one day.");
        }

        if (options.RollupInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("RollupInterval must be at least one second.");
        }

        if (options.MinuteRollupLookback < options.RollupInterval || options.HourRollupLookback < options.RollupInterval)
        {
            failures.Add("The rollup lookbacks must be at least as long as RollupInterval, or some records would never be totalled.");
        }

        if (options.Store == TelemetryStoreKind.Postgres && string.IsNullOrWhiteSpace(options.Postgres.ConnectionString))
        {
            failures.Add("Postgres.ConnectionString is required when Store is Postgres.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
