using Fusebox.Application.Reconciliation;
using Fusebox.Domain;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fusebox.Infrastructure.Telemetry.Postgres;

/// <summary>What Fusebox charged in a period, per model, summed from the per-hour totals the telemetry pipeline keeps.</summary>
internal sealed class PostgresUsageCostReader : IUsageCostReader, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _timeoutSeconds;

    public PostgresUsageCostReader(IOptions<TelemetryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = NpgsqlDataSource.Create(options.Value.Postgres.ConnectionString);
        _timeoutSeconds = (int)options.Value.Postgres.CommandTimeout.TotalSeconds;
    }

    public async ValueTask<IReadOnlyDictionary<string, Money>> CostByModelAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT model, sum(cost_micro_usd)
            FROM rollups_hour
            WHERE bucket >= @start AND bucket < @end
            GROUP BY model
            HAVING sum(cost_micro_usd) > 0
            """);
        command.CommandTimeout = _timeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = periodStart.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.TimestampTz) { Value = periodEnd.UtcDateTime });

        var costs = new Dictionary<string, Money>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            costs[reader.GetString(0)] = Money.FromMicroDollars(reader.GetInt64(1));
        }

        return costs;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
