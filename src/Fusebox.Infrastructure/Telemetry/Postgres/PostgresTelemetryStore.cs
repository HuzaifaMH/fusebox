using System.Globalization;
using System.Text;
using Fusebox.Application.Telemetry;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fusebox.Infrastructure.Telemetry.Postgres;

/// <summary>
/// Keeps request records in PostgreSQL: a table partitioned by day, plus per-minute and per-hour totals computed from it.
/// </summary>
internal sealed partial class PostgresTelemetryStore : ITelemetryStore, IAsyncDisposable
{
    internal const string SchemaResourcePrefix = "Fusebox.Infrastructure.Telemetry.Postgres.Schema.";

    // Arbitrary constants shared by every gateway instance, so that they take turns at the work each one guards.
    internal const long SchemaLockKey = 7_461_002;
    private const long PartitionLockKey = 7_461_003;
    private const long RollupLockKeyBase = 7_461_100;

    private const string PartitionPrefix = "requests_";
    private const string PartitionDateFormat = "yyyyMMdd";

    // A repeated save of the same record is ignored, so a batch whose outcome is unknown can simply be sent again.
    private const string InsertSql = """
        INSERT INTO requests (id, started_at, tenant_id, route_id, model, disposition, denial_reason, status_code,
                              duration_ms, is_streaming, input_tokens, output_tokens, cost_micro_usd, reserved_micro_usd, is_repeat)
        SELECT * FROM unnest(@id, @started_at, @tenant_id, @route_id, @model, @disposition, @denial_reason, @status_code,
                             @duration_ms, @is_streaming, @input_tokens, @output_tokens, @cost, @reserved, @is_repeat)
        ON CONFLICT (id, started_at) DO NOTHING
        """;

    private const string ListPartitionsSql = """
        SELECT c.relname
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'requests'::regclass
        """;

    private static readonly string MinuteRollupSql = BuildRollupSql("rollups_minute", "minute");
    private static readonly string HourRollupSql = BuildRollupSql("rollups_hour", "hour");

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOptions _options;
    private readonly ILogger<PostgresTelemetryStore> _logger;

    private readonly Lock _gate = new();
    private readonly HashSet<DateOnly> _knownPartitions = [];
    private Task? _prepared;

    public PostgresTelemetryStore(IOptions<TelemetryOptions> options, ILogger<PostgresTelemetryStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Postgres;
        _logger = logger;
        _dataSource = NpgsqlDataSource.Create(_options.ConnectionString);
    }

    public async ValueTask WriteAsync(IReadOnlyList<RequestRecord> batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Count == 0)
        {
            return;
        }

        await EnsurePreparedAsync(cancellationToken);
        await EnsurePartitionsAsync(batch.Select(r => DayOf(r.StartedAt)).Distinct().ToList(), cancellationToken);

        await using var command = _dataSource.CreateCommand(InsertSql);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;

        AddArray(command, "id", NpgsqlDbType.Uuid, batch.Select(r => r.Id).ToArray());
        AddArray(command, "started_at", NpgsqlDbType.TimestampTz, batch.Select(r => r.StartedAt.UtcDateTime).ToArray());
        AddArray(command, "tenant_id", NpgsqlDbType.Text, batch.Select(r => r.TenantId.Value).ToArray());
        AddArray(command, "route_id", NpgsqlDbType.Text, batch.Select(r => r.RouteId).ToArray());
        AddArray(command, "model", NpgsqlDbType.Text, batch.Select(r => r.Model).ToArray());
        AddArray(command, "disposition", NpgsqlDbType.Text, batch.Select(r => TelemetryNames.Of(r.Disposition)).ToArray());
        AddArray(command, "denial_reason", NpgsqlDbType.Text, batch.Select(r => r.DenialReason is { } reason ? TelemetryNames.Of(reason) : null).ToArray());
        AddArray(command, "status_code", NpgsqlDbType.Smallint, batch.Select(r => (short)r.StatusCode).ToArray());
        AddArray(command, "duration_ms", NpgsqlDbType.Integer, batch.Select(r => (int)Math.Min(r.Duration.TotalMilliseconds, int.MaxValue)).ToArray());
        AddArray(command, "is_streaming", NpgsqlDbType.Boolean, batch.Select(r => r.IsStreaming).ToArray());
        AddArray(command, "input_tokens", NpgsqlDbType.Bigint, batch.Select(r => r.Usage?.InputTokens).ToArray());
        AddArray(command, "output_tokens", NpgsqlDbType.Bigint, batch.Select(r => r.Usage?.OutputTokens).ToArray());
        AddArray(command, "cost", NpgsqlDbType.Bigint, batch.Select(r => r.Cost.MicroDollars).ToArray());
        AddArray(command, "reserved", NpgsqlDbType.Bigint, batch.Select(r => r.Reserved.MicroDollars).ToArray());
        AddArray(command, "is_repeat", NpgsqlDbType.Boolean, batch.Select(r => r.IsRepeat).ToArray());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<bool> RollUpAsync(
        RollupGranularity granularity,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Only one gateway instance computes a given kind of total at a time. The lock ends with the transaction.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", RollupLockKeyBase + (int)granularity);

            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return false;
            }
        }

        await using var command = new NpgsqlCommand(granularity == RollupGranularity.Minute ? MinuteRollupSql : HourRollupSql, connection, transaction)
        {
            CommandTimeout = (int)_options.CommandTimeout.TotalSeconds,
        };
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = windowStart.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = windowEnd.UtcDateTime });

        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async ValueTask<int> DropRawBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        var firstDayToKeep = DayOf(cutoff);
        var expired = new List<DateOnly>();

        await using (var command = _dataSource.CreateCommand(ListPartitionsSql))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (TryParsePartitionDay(reader.GetString(0), out var day) && day < firstDayToKeep)
                {
                    expired.Add(day);
                }
            }
        }

        foreach (var day in expired)
        {
            // The name is built from a date, never from input, so it cannot carry anything but digits.
            var partition = PartitionName(day);
            await using var drop = _dataSource.CreateCommand($"DROP TABLE IF EXISTS {partition}");
            await drop.ExecuteNonQueryAsync(cancellationToken);

            lock (_gate)
            {
                _knownPartitions.Remove(day);
            }

            LogPartitionDropped(partition);
        }

        return expired.Count;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    // The tables are created the first time they are needed. If that fails it is tried again on the next use.
    private Task EnsurePreparedAsync(CancellationToken cancellationToken)
    {
        Task prepared;

        lock (_gate)
        {
            prepared = _prepared ??= PrepareAsync();
        }

        return prepared.WaitAsync(cancellationToken);
    }

    private async Task PrepareAsync()
    {
        try
        {
            if (_options.ApplySchemaOnStartup)
            {
                await PostgresSchema.ApplyAsync(_dataSource, SchemaResourcePrefix, SchemaLockKey, _options.CommandTimeout, CancellationToken.None);
            }
        }
        catch
        {
            lock (_gate)
            {
                _prepared = null;
            }

            throw;
        }
    }

    // A day's partition is created just before its first record is saved, so a record never lands nowhere.
    private async Task EnsurePartitionsAsync(IReadOnlyList<DateOnly> days, CancellationToken cancellationToken)
    {
        List<DateOnly> missing;

        lock (_gate)
        {
            missing = [.. days.Where(day => !_knownPartitions.Contains(day))];
        }

        if (missing.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({PartitionLockKey})", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var day in missing)
        {
            await using var create = new NpgsqlCommand(CreatePartitionSql(day), connection, transaction);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        lock (_gate)
        {
            _knownPartitions.UnionWith(missing);
        }
    }

    private static string CreatePartitionSql(DateOnly day) => string.Create(
        CultureInfo.InvariantCulture,
        $"CREATE TABLE IF NOT EXISTS {PartitionName(day)} PARTITION OF requests FOR VALUES FROM ('{day:yyyy-MM-dd}T00:00:00Z') TO ('{day.AddDays(1):yyyy-MM-dd}T00:00:00Z')");

    private static string PartitionName(DateOnly day) =>
        PartitionPrefix + day.ToString(PartitionDateFormat, CultureInfo.InvariantCulture);

    private static bool TryParsePartitionDay(string tableName, out DateOnly day)
    {
        day = default;

        return tableName.StartsWith(PartitionPrefix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(tableName.AsSpan(PartitionPrefix.Length), PartitionDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    private static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    private static void AddArray<T>(NpgsqlCommand command, string name, NpgsqlDbType elementType, T[] values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | elementType) { Value = values });

    /// <summary>
    /// Totals every bucket overlapping the window from the raw records and replaces what was there. Replacing, not adding,
    /// is what makes the job safe to repeat. Latency figures cover forwarded calls only: a refused call has no vendor latency.
    /// </summary>
    private static string BuildRollupSql(string table, string unit)
    {
        var forwarded = $"disposition = '{TelemetryNames.Forwarded}'";
        var bounds = LatencyBuckets.UpperBoundsMilliseconds;

        var histogram = new StringBuilder();
        for (var i = 0; i <= bounds.Count; i++)
        {
            var lower = i == 0 ? string.Empty : $" AND duration_ms > {bounds[i - 1]}";
            var upper = i == bounds.Count ? string.Empty : $" AND duration_ms <= {bounds[i]}";
            histogram.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? string.Empty : ",\n                ")}count(*) FILTER (WHERE {forwarded}{lower}{upper})");
        }

        return $"""
            INSERT INTO {table} (bucket, tenant_id, route_id, model, requests, forwarded, denied, rejected, unavailable, failed,
                                 errors, input_tokens, output_tokens, cost_micro_usd, duration_ms_sum, duration_ms_max, duration_buckets, repeats)
            SELECT date_trunc('{unit}', started_at), tenant_id, route_id, coalesce(model, ''),
                   count(*),
                   count(*) FILTER (WHERE {forwarded}),
                   count(*) FILTER (WHERE disposition = '{TelemetryNames.Denied}'),
                   count(*) FILTER (WHERE disposition = '{TelemetryNames.Rejected}'),
                   count(*) FILTER (WHERE disposition = '{TelemetryNames.Unavailable}'),
                   count(*) FILTER (WHERE disposition = '{TelemetryNames.Failed}'),
                   count(*) FILTER (WHERE {forwarded} AND status_code >= 400),
                   coalesce(sum(input_tokens), 0),
                   coalesce(sum(output_tokens), 0),
                   sum(cost_micro_usd),
                   coalesce(sum(duration_ms) FILTER (WHERE {forwarded}), 0),
                   coalesce(max(duration_ms) FILTER (WHERE {forwarded}), 0),
                   ARRAY[{histogram}]::bigint[],
                   count(*) FILTER (WHERE {forwarded} AND is_repeat)
            FROM requests
            WHERE started_at >= date_trunc('{unit}', @from)
              AND started_at < date_trunc('{unit}', @to) + interval '1 {unit}'
            GROUP BY 1, 2, 3, 4
            ON CONFLICT (bucket, tenant_id, route_id, model) DO UPDATE SET
                requests = EXCLUDED.requests, forwarded = EXCLUDED.forwarded, denied = EXCLUDED.denied,
                rejected = EXCLUDED.rejected, unavailable = EXCLUDED.unavailable, failed = EXCLUDED.failed,
                errors = EXCLUDED.errors, input_tokens = EXCLUDED.input_tokens, output_tokens = EXCLUDED.output_tokens,
                cost_micro_usd = EXCLUDED.cost_micro_usd, duration_ms_sum = EXCLUDED.duration_ms_sum,
                duration_ms_max = EXCLUDED.duration_ms_max, duration_buckets = EXCLUDED.duration_buckets,
                repeats = EXCLUDED.repeats
            """;
    }

    [LoggerMessage(EventId = 5201, Level = LogLevel.Information,
        Message = "Dropped the expired partition {Partition}")]
    private partial void LogPartitionDropped(string partition);
}
