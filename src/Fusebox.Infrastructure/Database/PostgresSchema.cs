using Npgsql;

namespace Fusebox.Infrastructure.Database;

/// <summary>Creates a feature's tables from the SQL scripts embedded in the assembly.</summary>
internal static class PostgresSchema
{
    /// <summary>
    /// Runs every embedded script whose resource name starts with <paramref name="resourcePrefix"/>, in name order,
    /// in one transaction. The scripts must be idempotent. A lock makes several gateway instances starting together
    /// take turns instead of colliding while creating the same table.
    /// </summary>
    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        string resourcePrefix,
        long lockKey,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, $"SELECT pg_advisory_xact_lock({lockKey})", commandTimeout, cancellationToken);

        foreach (var script in ReadScripts(resourcePrefix))
        {
            await ExecuteAsync(connection, transaction, script, commandTimeout, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        TimeSpan commandTimeout,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = (int)commandTimeout.TotalSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IEnumerable<string> ReadScripts(string resourcePrefix)
    {
        var assembly = typeof(PostgresSchema).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(resourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded schema script '{name}' was not found.");
            using var reader = new StreamReader(stream);
            yield return reader.ReadToEnd();
        }
    }
}
