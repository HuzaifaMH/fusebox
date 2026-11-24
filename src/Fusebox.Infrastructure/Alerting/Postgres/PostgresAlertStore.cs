using Fusebox.Application.Alerting;
using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Database;
using Fusebox.Infrastructure.Detection.Postgres;
using Fusebox.Infrastructure.Diagnosis.Postgres;
using Fusebox.Infrastructure.Telemetry;
using Fusebox.Infrastructure.Telemetry.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fusebox.Infrastructure.Alerting.Postgres;

/// <summary>
/// Keeps alerts and the notifications waiting to be sent in PostgreSQL, and reads what is wrong from the anomalies and
/// telemetry kept in the same database.
/// </summary>
internal sealed class PostgresAlertStore : IAlertStore, IAlertFeed, IAsyncDisposable
{
    private const string SchemaResourcePrefix = "Fusebox.Infrastructure.Alerting.Postgres.Schema.";

    // Arbitrary constants shared by every gateway instance.
    private const long SchemaLockKey = 7_461_006;
    private const long SourcesLockKey = 7_461_400;

    private const string UniqueViolation = "23505";

    private const string AlertColumns = "id, fingerprint, kind, severity, title, summary, details::text, revision, opened_at, updated_at";

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOptions _options;

    private readonly Lock _gate = new();
    private Task? _prepared;

    public PostgresAlertStore(IOptions<TelemetryOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Postgres;
        _dataSource = NpgsqlDataSource.Create(_options.ConnectionString);
    }

    public async ValueTask<AlertRecord?> FindOpenAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = Command($"SELECT {AlertColumns} FROM alerts WHERE fingerprint = @fingerprint AND status = 'open'");
        command.Parameters.AddWithValue("fingerprint", fingerprint);

        var found = await ReadAlertsAsync(command, cancellationToken);
        return found.Count == 0 ? null : found[0];
    }

    public async ValueTask<IReadOnlyList<AlertRecord>> FindOpenByKindAsync(AlertKind kind, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = Command($"SELECT {AlertColumns} FROM alerts WHERE kind = @kind AND status = 'open'");
        command.Parameters.AddWithValue("kind", AlertNames.Of(kind));

        return await ReadAlertsAsync(command, cancellationToken);
    }

    public async ValueTask<DateTimeOffset?> FindLastResolvedAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = Command("SELECT max(resolved_at) FROM alerts WHERE fingerprint = @fingerprint AND status = 'resolved'");
        command.Parameters.AddWithValue("fingerprint", fingerprint);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateTime at ? AsUtc(at) : null;
    }

    public async ValueTask<bool> TryOpenAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(deliveries);

        await EnsurePreparedAsync(cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO alerts (id, fingerprint, kind, severity, status, title, summary, details, revision, opened_at, updated_at)
                VALUES (@id, @fingerprint, @kind, @severity, 'open', @title, @summary, @details, @revision, @opened_at, @updated_at)
                """, connection, transaction))
            {
                BindAlert(insert, alert);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException exception) when (exception.SqlState == UniqueViolation)
        {
            // Another gateway instance opened an alert for this incident first.
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        await InsertDeliveriesAsync(connection, transaction, alert.Id, deliveries, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async ValueTask UpdateAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(deliveries);

        await EnsurePreparedAsync(cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var update = new NpgsqlCommand("""
            UPDATE alerts
            SET severity = @severity, title = @title, summary = @summary, details = @details, revision = @revision, updated_at = @updated_at
            WHERE id = @id AND status = 'open'
            """, connection, transaction))
        {
            BindAlert(update, alert);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertDeliveriesAsync(connection, transaction, alert.Id, deliveries, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask ResolveAsync(Guid alertId, DateTimeOffset at, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveries);

        await EnsurePreparedAsync(cancellationToken);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var resolve = new NpgsqlCommand(
            "UPDATE alerts SET status = 'resolved', resolved_at = @at, updated_at = @at WHERE id = @id AND status = 'open'",
            connection,
            transaction))
        {
            resolve.Parameters.AddWithValue("id", alertId);
            resolve.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });
            await resolve.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertDeliveriesAsync(connection, transaction, alertId, deliveries, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<PendingDelivery>> ClaimDueAsync(DateTimeOffset now, int limit, TimeSpan claimFor, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        // SKIP LOCKED lets several gateway instances take different rows at the same moment instead of waiting on each other.
        await using var command = Command("""
            UPDATE alert_deliveries d
            SET attempts = d.attempts + 1, claimed_until = @claimed_until
            FROM (
                SELECT id FROM alert_deliveries
                WHERE status = 'pending' AND next_attempt_at <= @now AND (claimed_until IS NULL OR claimed_until < @now)
                ORDER BY next_attempt_at, created_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            ) due
            WHERE d.id = due.id
            RETURNING d.id, d.channel, d.attempts, d.payload::text
            """);
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("claimed_until", NpgsqlDbType.TimestampTz) { Value = (now + claimFor).UtcDateTime });
        command.Parameters.AddWithValue("limit", limit);

        var claimed = new List<PendingDelivery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new PendingDelivery(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt32(2),
                AlertNames.DeserializeNotification(reader.GetString(3))));
        }

        return [.. claimed.OrderBy(d => d.Notification.At)];
    }

    public async ValueTask MarkSentAsync(Guid deliveryId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE alert_deliveries SET status = 'sent', sent_at = @at, claimed_until = NULL, last_error = NULL WHERE id = @id");
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask ScheduleRetryAsync(Guid deliveryId, DateTimeOffset nextAttemptAt, string reason, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE alert_deliveries SET next_attempt_at = @next, claimed_until = NULL, last_error = @reason WHERE id = @id");
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = nextAttemptAt.UtcDateTime });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask MarkFailedAsync(Guid deliveryId, string reason, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE alert_deliveries SET status = 'failed', claimed_until = NULL, last_error = @reason WHERE id = @id");
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        await EnsurePreparedAsync(cancellationToken);

        // A session-level lock lives as long as this connection, so it is held while the work uses other connections.
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var acquire = new NpgsqlCommand($"SELECT pg_try_advisory_lock({SourcesLockKey})", connection))
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
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({SourcesLockKey})", connection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public async ValueTask<IReadOnlyList<AnomalyAlertState>> LoadAnomaliesAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = Command("""
            SELECT a.id, a.tenant_id, a.route_id, a.signal, a.state, a.opened_at, a.closed_at, a.baseline, a.basis,
                   a.opened_value, a.peak_value, a.peak_ratio, d.diagnosis, d.remediation, d.confidence, d.source
            FROM anomalies a
            LEFT JOIN anomaly_diagnoses d ON d.anomaly_id = a.id
            WHERE a.state = 'open' OR a.closed_at >= @since
            ORDER BY a.opened_at
            """);
        command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = since.UtcDateTime });

        var states = new List<AnomalyAlertState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var diagnosis = reader.IsDBNull(12)
                ? null
                : new DiagnosisSummary(
                    reader.GetString(12),
                    reader.GetString(13),
                    Enum.Parse<NarrationConfidence>(reader.GetString(14), ignoreCase: true),
                    reader.GetString(15) == "llm" ? NarrationSource.Llm : NarrationSource.Template);

            states.Add(new AnomalyAlertState(
                reader.GetGuid(0),
                new SeriesKey(TenantId.Parse(reader.GetString(1)), reader.GetString(2)),
                DetectionNames.SignalOf(reader.GetString(3)),
                reader.GetString(4) == "open",
                AsUtc(reader.GetDateTime(5)),
                reader.IsDBNull(6) ? null : AsUtc(reader.GetDateTime(6)),
                reader.GetDouble(7),
                DetectionNames.BasisOf(reader.GetString(8)),
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.GetDouble(11),
                diagnosis));
        }

        return states;
    }

    public async ValueTask<IReadOnlyList<BudgetBlock>> LoadBudgetBlocksAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        await EnsurePreparedAsync(cancellationToken);

        await using var command = Command("""
            SELECT tenant_id, count(*), max(started_at)
            FROM requests
            WHERE denial_reason = 'budget_exceeded' AND started_at >= @since
            GROUP BY tenant_id
            ORDER BY tenant_id
            """);
        command.Parameters.Add(new NpgsqlParameter("since", NpgsqlDbType.TimestampTz) { Value = since.UtcDateTime });

        var blocks = new List<BudgetBlock>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            blocks.Add(new BudgetBlock(TenantId.Parse(reader.GetString(0)), reader.GetInt64(1), AsUtc(reader.GetDateTime(2))));
        }

        return blocks;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private NpgsqlCommand Command(string sql)
    {
        var command = _dataSource.CreateCommand(sql);
        command.CommandTimeout = (int)_options.CommandTimeout.TotalSeconds;
        return command;
    }

    private static void BindAlert(NpgsqlCommand command, AlertRecord alert)
    {
        command.Parameters.AddWithValue("id", alert.Id);
        command.Parameters.AddWithValue("fingerprint", alert.Fingerprint);
        command.Parameters.AddWithValue("kind", AlertNames.Of(alert.Kind));
        command.Parameters.AddWithValue("severity", AlertNames.Of(alert.Severity));
        command.Parameters.AddWithValue("title", alert.Title);
        command.Parameters.AddWithValue("summary", alert.Summary);
        command.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = AlertNames.Serialize(alert.Details) });
        command.Parameters.AddWithValue("revision", alert.Revision);
        command.Parameters.Add(new NpgsqlParameter("opened_at", NpgsqlDbType.TimestampTz) { Value = alert.OpenedAt.UtcDateTime });
        command.Parameters.Add(new NpgsqlParameter("updated_at", NpgsqlDbType.TimestampTz) { Value = alert.UpdatedAt.UtcDateTime });
    }

    // A notification that was already queued is not queued again, which makes raising the same thing twice harmless.
    private static async Task InsertDeliveriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid alertId,
        IReadOnlyList<DeliveryRequest> deliveries,
        CancellationToken cancellationToken)
    {
        foreach (var delivery in deliveries)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO alert_deliveries (id, alert_id, event, severity, channel, revision, status, next_attempt_at, payload)
                VALUES (@id, @alert_id, @event, @severity, @channel, @revision, 'pending', @next, @payload)
                ON CONFLICT (alert_id, event, severity, channel, revision) DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", Guid.CreateVersion7(delivery.Notification.At));
            insert.Parameters.AddWithValue("alert_id", alertId);
            insert.Parameters.AddWithValue("event", AlertNames.Of(delivery.Event));
            insert.Parameters.AddWithValue("severity", AlertNames.Of(delivery.Severity));
            insert.Parameters.AddWithValue("channel", delivery.Channel);
            insert.Parameters.AddWithValue("revision", delivery.Revision);
            insert.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.TimestampTz) { Value = delivery.Notification.At.UtcDateTime });
            insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = AlertNames.Serialize(delivery.Notification) });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<AlertRecord>> ReadAlertsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var alerts = new List<AlertRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            alerts.Add(new AlertRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                AlertNames.KindOf(reader.GetString(2)),
                AlertNames.SeverityOf(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                AlertNames.DeserializeDetails(reader.GetString(6)),
                reader.GetString(7),
                AsUtc(reader.GetDateTime(8)),
                AsUtc(reader.GetDateTime(9))));
        }

        return alerts;
    }

    // The alert tables refer to the telemetry, anomaly and diagnosis tables, so all four schemas are created.
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
                await PostgresSchema.ApplyAsync(_dataSource, PostgresDiagnosisStore.SchemaResourcePrefix, PostgresDiagnosisStore.SchemaLockKey, _options.CommandTimeout, CancellationToken.None);
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
