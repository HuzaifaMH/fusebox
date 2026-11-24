using Fusebox.Application.Detection;
using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Database;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fusebox.Infrastructure.Detection.Postgres;

/// <summary>
/// Reads the per-minute totals the telemetry pipeline keeps, and keeps the anomalies the detector finds, in the same
/// PostgreSQL database.
/// </summary>
internal sealed class PostgresAnomalyStore : IAnomalyStore, IAsyncDisposable
{
    internal const string SchemaResourcePrefix = "Fusebox.Infrastructure.Detection.Postgres.Schema.";

    // Arbitrary constants shared by every gateway instance.
    internal const long SchemaLockKey = 7_461_004;
    private const long DetectionLockKey = 7_461_200;

    private const string MinuteColumns = "tenant_id, route_id, bucket, requests, forwarded, errors, input_tokens, output_tokens, cost_micro_usd, duration_buckets, repeats";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOptions _options;

    private readonly Lock _gate = new();
    private Task? _prepared;

    public PostgresAnomalyStore(IOptions<TelemetryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Postgres;
        _dataSource = NpgsqlDataSource.Create(_options.ConnectionString);
    }

    public async ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await EnsurePreparedAsync(cancellationToken);

        // A session-level lock lives as long as this connection, so it is held while the work uses other connections.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var acquire = new NpgsqlCommand($"SELECT pg_try_advisory_lock({DetectionLockKey})", connection))
        {
            if (await acquire.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return false;
            }
        }

        try
        {
            await work(cancellationToken);
            return true;
        }
        finally
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({DetectionLockKey})", connection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public async ValueTask<IReadOnlyList<MinuteRow>> LoadMinutesAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand($"SELECT {MinuteColumns} FROM rollups_minute WHERE bucket >= @start AND bucket < @end");
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        command.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = windowStart.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.TimestampTz) { Value = windowEnd.UtcDateTime });

        return await ReadMinutesAsync(command, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MinuteRow>> LoadHistoryAsync(
        IReadOnlyCollection<SeriesKey> series,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ranges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(ranges);

        if (series.Count == 0 || ranges.Count == 0)
        {
            return [];
        }

        await EnsurePreparedAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand($"""
            SELECT m.tenant_id, m.route_id, m.bucket, m.requests, m.forwarded, m.errors, m.input_tokens, m.output_tokens, m.cost_micro_usd, m.duration_buckets, m.repeats
            FROM rollups_minute m
            JOIN unnest(@range_starts, @range_ends) AS r(range_start, range_end)
              ON m.bucket >= r.range_start AND m.bucket < r.range_end
            WHERE (m.tenant_id, m.route_id) IN (SELECT s.tenant_id, s.route_id FROM unnest(@tenants, @routes) AS s(tenant_id, route_id))
            """);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        AddArray(command, "range_starts", NpgsqlDbType.TimestampTz, ranges.Select(r => r.Start.UtcDateTime).ToArray());
        AddArray(command, "range_ends", NpgsqlDbType.TimestampTz, ranges.Select(r => r.End.UtcDateTime).ToArray());
        AddArray(command, "tenants", NpgsqlDbType.Text, series.Select(s => s.TenantId.Value).ToArray());
        AddArray(command, "routes", NpgsqlDbType.Text, series.Select(s => s.RouteId).ToArray());

        return await ReadMinutesAsync(command, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<OpenAnomaly>> LoadOpenAsync(CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand("""
            SELECT id, tenant_id, route_id, signal, opened_at, baseline, basis, opened_value, peak_value, peak_ratio, calm_evaluations, last_seen_at
            FROM anomalies
            WHERE state = 'open'
            """);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;

        var open = new List<OpenAnomaly>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            open.Add(new OpenAnomaly(
                reader.GetGuid(0),
                new SeriesKey(TenantId.Parse(reader.GetString(1)), reader.GetString(2)),
                DetectionNames.SignalOf(reader.GetString(3)),
                AsUtc(reader.GetDateTime(4)),
                reader.GetDouble(5),
                DetectionNames.BasisOf(reader.GetString(6)),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetInt32(10),
                AsUtc(reader.GetDateTime(11))));
        }

        return open;
    }

    public async ValueTask<IReadOnlyDictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>> LoadRecentlyClosedAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand("""
            SELECT tenant_id, route_id, signal, max(closed_at)
            FROM anomalies
            WHERE state = 'closed' AND closed_at >= @since
            GROUP BY tenant_id, route_id, signal
            """);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = since.UtcDateTime });

        var closed = new Dictionary<(SeriesKey Series, Signal Signal), DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            closed[(new SeriesKey(TenantId.Parse(reader.GetString(0)), reader.GetString(1)), DetectionNames.SignalOf(reader.GetString(2)))] =
                AsUtc(reader.GetDateTime(3));
        }

        return closed;
    }

    public async ValueTask SaveAsync(AnomalyChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await EnsurePreparedAsync(cancellationToken);

        await using var command = change switch
        {
            AnomalyOpenedChange opened => OpenCommand(opened.Anomaly),
            AnomalyProgressedChange progressed => ProgressCommand(progressed),
            AnomalyClosedChange closed => CloseCommand(closed),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown change."),
        };

        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private NpgsqlCommand OpenCommand(OpenAnomaly anomaly)
    {
        var command = _dataSource.CreateCommand("""
            INSERT INTO anomalies (id, tenant_id, route_id, signal, state, opened_at, last_seen_at, basis, baseline, opened_value, peak_value, peak_ratio, calm_evaluations)
            VALUES (@id, @tenant, @route, @signal, 'open', @opened_at, @last_seen, @basis, @baseline, @opened_value, @peak_value, @peak_ratio, @calm)
            """);
        command.Parameters.AddWithValue("id", anomaly.Id);
        command.Parameters.AddWithValue("tenant", anomaly.Series.TenantId.Value);
        command.Parameters.AddWithValue("route", anomaly.Series.RouteId);
        command.Parameters.AddWithValue("signal", DetectionNames.Of(anomaly.Signal));
        command.Parameters.Add(new NpgsqlParameter("opened_at", NpgsqlDbType.TimestampTz) { Value = anomaly.OpenedAt.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("last_seen", NpgsqlDbType.TimestampTz) { Value = anomaly.LastSeenAt.UtcDateTime });
        command.Parameters.AddWithValue("basis", DetectionNames.Of(anomaly.Basis));
        command.Parameters.AddWithValue("baseline", anomaly.Baseline);
        command.Parameters.AddWithValue("opened_value", anomaly.OpenedValue);
        command.Parameters.AddWithValue("peak_value", anomaly.PeakValue);
        command.Parameters.AddWithValue("peak_ratio", anomaly.PeakRatio);
        command.Parameters.AddWithValue("calm", anomaly.CalmEvaluations);
        return command;
    }

    private NpgsqlCommand ProgressCommand(AnomalyProgressedChange change)
    {
        var command = _dataSource.CreateCommand("""
            UPDATE anomalies
            SET peak_value = @peak_value, peak_ratio = @peak_ratio, calm_evaluations = @calm, last_seen_at = @last_seen
            WHERE id = @id AND state = 'open'
            """);
        command.Parameters.AddWithValue("id", change.Id);
        command.Parameters.AddWithValue("peak_value", change.PeakValue);
        command.Parameters.AddWithValue("peak_ratio", change.PeakRatio);
        command.Parameters.AddWithValue("calm", change.CalmEvaluations);
        command.Parameters.Add(new NpgsqlParameter("last_seen", NpgsqlDbType.TimestampTz) { Value = change.LastSeenAt.UtcDateTime });
        return command;
    }

    private NpgsqlCommand CloseCommand(AnomalyClosedChange change)
    {
        var command = _dataSource.CreateCommand("UPDATE anomalies SET state = 'closed', closed_at = @closed_at, last_seen_at = @closed_at WHERE id = @id AND state = 'open'");
        command.Parameters.AddWithValue("id", change.Id);
        command.Parameters.Add(new NpgsqlParameter("closed_at", NpgsqlDbType.TimestampTz) { Value = change.ClosedAt.UtcDateTime });
        return command;
    }

    private static async Task<IReadOnlyList<MinuteRow>> ReadMinutesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<MinuteRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MinuteRow(
                new SeriesKey(TenantId.Parse(reader.GetString(0)), reader.GetString(1)),
                new MinuteStats(
                    AsUtc(reader.GetDateTime(2)),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetFieldValue<long[]>(9),
                    reader.GetInt64(10))));
        }

        return rows;
    }

    // The detection tables live beside the telemetry tables they read, so both are created if either is missing.
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
                await PostgresSchema.ApplyAsync(_dataSource, PostgresTelemetryStore.SchemaResourcePrefix, PostgresTelemetryStore.SchemaLockKey, _options.CommandTimeout, CancellationToken.None);
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

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static void AddArray<T>(NpgsqlCommand command, string name, NpgsqlDbType elementType, T[] values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | elementType) { Value = values });
}
