using System.Text.Json;
using System.Text.Json.Serialization;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Database;
using Fusebox.Infrastructure.Detection.Postgres;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fusebox.Infrastructure.Diagnosis.Postgres;

/// <summary>
/// Reads the per-minute totals to compare, finds anomalies that need a diagnosis, and keeps the diagnoses, all in the
/// same PostgreSQL database as the telemetry and the anomalies.
/// </summary>
internal sealed class PostgresDiagnosisStore : IDiagnosisStore, IEvidenceSource, IAsyncDisposable
{
    internal const string SchemaResourcePrefix = "Fusebox.Infrastructure.Diagnosis.Postgres.Schema.";

    // Arbitrary constants shared by every gateway instance.
    internal const long SchemaLockKey = 7_461_005;
    private const long DiagnosisLockKey = 7_461_300;

    private static readonly JsonSerializerOptions EvidenceJson = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOptions _options;

    private readonly Lock _gate = new();
    private Task? _prepared;

    public PostgresDiagnosisStore(IOptions<TelemetryOptions> options)
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

        await using (var acquire = new NpgsqlCommand($"SELECT pg_try_advisory_lock({DiagnosisLockKey})", connection))
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
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({DiagnosisLockKey})", connection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public async ValueTask<IReadOnlyList<AnomalyToDiagnose>> LoadUndiagnosedAsync(DateTimeOffset openedSince, int limit, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand("""
            SELECT a.id, a.tenant_id, a.route_id, a.signal, a.opened_at
            FROM anomalies a
            LEFT JOIN anomaly_diagnoses d ON d.anomaly_id = a.id
            WHERE d.anomaly_id IS NULL AND a.opened_at >= @since
            ORDER BY a.opened_at
            LIMIT @limit
            """);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = openedSince.UtcDateTime });
        command.Parameters.AddWithValue("limit", limit);

        var pending = new List<AnomalyToDiagnose>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            pending.Add(new AnomalyToDiagnose(
                reader.GetGuid(0),
                new SeriesKey(TenantId.Parse(reader.GetString(1)), reader.GetString(2)),
                DetectionNames.SignalOf(reader.GetString(3)),
                AsUtc(reader.GetDateTime(4))));
        }

        return pending;
    }

    public async ValueTask<int> CountLlmNarrationsAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand("SELECT count(*) FROM anomaly_diagnoses WHERE source = 'llm' AND diagnosed_at >= @since");
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = since.UtcDateTime });

        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async ValueTask SaveAsync(DiagnosisRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await EnsurePreparedAsync(cancellationToken);

        var narration = record.Narration;

        await using var command = _dataSource.CreateCommand("""
            INSERT INTO anomaly_diagnoses (anomaly_id, diagnosed_at, source, model, confidence, diagnosis, remediation, evidence_ids, fallback_reason, unexplained_share, is_thin, evidence)
            VALUES (@id, @diagnosed_at, @source, @model, @confidence, @diagnosis, @remediation, @evidence_ids, @fallback_reason, @unexplained, @thin, @evidence)
            ON CONFLICT (anomaly_id) DO NOTHING
            """);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        command.Parameters.AddWithValue("id", record.AnomalyId);
        command.Parameters.Add(new NpgsqlParameter("diagnosed_at", NpgsqlDbType.TimestampTz) { Value = record.DiagnosedAt.UtcDateTime });
        command.Parameters.AddWithValue("source", narration.Source == NarrationSource.Llm ? "llm" : "template");
        command.Parameters.Add(new NpgsqlParameter("model", NpgsqlDbType.Text) { Value = (object?)narration.Model ?? DBNull.Value });
        command.Parameters.AddWithValue("confidence", narration.Confidence.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("diagnosis", narration.Diagnosis);
        command.Parameters.AddWithValue("remediation", narration.Remediation);
        command.Parameters.Add(new NpgsqlParameter("evidence_ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = narration.EvidenceIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("fallback_reason", NpgsqlDbType.Text) { Value = (object?)narration.FallbackReason ?? DBNull.Value });
        command.Parameters.AddWithValue("unexplained", record.Evidence.Unexplained);
        command.Parameters.AddWithValue("thin", record.Evidence.IsThin);
        command.Parameters.Add(new NpgsqlParameter("evidence", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(record.Evidence.Items, EvidenceJson) });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<PeriodTotals> LoadPeriodAsync(SeriesKey series, DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = _dataSource.CreateCommand("""
            SELECT model, bucket, requests, forwarded, denied, rejected, unavailable, failed, errors,
                   input_tokens, output_tokens, cost_micro_usd, duration_buckets, repeats
            FROM rollups_minute
            WHERE tenant_id = @tenant AND route_id = @route AND bucket >= @start AND bucket < @end
            """);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        command.Parameters.AddWithValue("tenant", series.TenantId.Value);
        command.Parameters.AddWithValue("route", series.RouteId);
        command.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = periodStart.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.TimestampTz) { Value = periodEnd.UtcDateTime });

        var byModel = new Dictionary<string, ModelTotals>();
        var minutes = new HashSet<DateTime>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var model = reader.GetString(0);
            minutes.Add(reader.GetDateTime(1));

            var row = new ModelTotals(
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetFieldValue<long[]>(12),
                reader.GetInt64(13));

            byModel[model] = byModel.TryGetValue(model, out var existing) ? existing.Plus(row) : row;
        }

        return new PeriodTotals(minutes.Count, (int)(periodEnd - periodStart).TotalMinutes, byModel);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    // The diagnosis tables refer to the anomalies and read the telemetry rollups, so all three schemas are created.
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
                await PostgresSchema.ApplyAsync(_dataSource, PostgresAnomalyStore.SchemaResourcePrefix, PostgresAnomalyStore.SchemaLockKey, _options.CommandTimeout, CancellationToken.None);
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
}
