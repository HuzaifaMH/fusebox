using Fusebox.Application.Admin;
using Fusebox.Domain;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fusebox.Infrastructure.Catalog.Postgres;

/// <summary>
/// Reads and writes the tenants and model prices tables that <see cref="PostgresCatalogSource"/> loads the catalog from.
/// The gateway notices a change at its next reload, which the admin service asks for right after a write.
/// </summary>
internal sealed class PostgresCatalogAdminStore : ICatalogAdminStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _timeoutSeconds;

    public PostgresCatalogAdminStore(IOptions<CatalogSourceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = NpgsqlDataSource.Create(options.Value.Postgres.ConnectionString);
        _timeoutSeconds = (int)options.Value.Postgres.CommandTimeout.TotalSeconds;
    }

    public async ValueTask<IReadOnlyList<TenantSummary>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT id, name, budget_limit_micro_usd, budget_window_seconds, enabled, created_at, updated_at, price_multiplier_bp
            FROM tenants ORDER BY id
            """);

        var tenants = new List<TenantSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            tenants.Add(ReadTenant(reader));
        }

        return tenants;
    }

    public async ValueTask<TenantSummary?> FindTenantAsync(TenantId id, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT id, name, budget_limit_micro_usd, budget_window_seconds, enabled, created_at, updated_at, price_multiplier_bp
            FROM tenants WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTenant(reader) : null;
    }

    public async ValueTask<bool> TryAddTenantAsync(NewTenant tenant, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        await using var command = Command("""
            INSERT INTO tenants (id, name, api_key_hash, budget_limit_micro_usd, budget_window_seconds, enabled, created_at, updated_at)
            VALUES (@id, @name, @hash, @limit, @window, true, @at, @at)
            ON CONFLICT (id) DO NOTHING
            """);
        command.Parameters.AddWithValue("id", tenant.Tenant.Id.Value);
        command.Parameters.AddWithValue("name", tenant.Tenant.Name);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, tenant.ApiKeyHash);
        command.Parameters.AddWithValue("limit", tenant.Tenant.Budget.Limit.MicroDollars);
        command.Parameters.AddWithValue("window", (int)tenant.Tenant.Budget.Window.TotalSeconds);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<bool> SetBudgetAsync(TenantId id, BudgetPolicy budget, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budget);

        await using var command = Command("""
            UPDATE tenants SET budget_limit_micro_usd = @limit, budget_window_seconds = @window, updated_at = @at WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("limit", budget.Limit.MicroDollars);
        command.Parameters.AddWithValue("window", (int)budget.Window.TotalSeconds);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<bool> SetEnabledAsync(TenantId id, bool enabled, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE tenants SET enabled = @enabled, updated_at = @at WHERE id = @id");
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<bool> ReplaceKeyHashAsync(TenantId id, string apiKeyHash, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE tenants SET api_key_hash = @hash, updated_at = @at WHERE id = @id");
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Char, apiKeyHash);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<bool> SetPriceMultiplierAsync(TenantId id, int basisPoints, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = Command("UPDATE tenants SET price_multiplier_bp = @bp, updated_at = @at WHERE id = @id");
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("bp", basisPoints);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at.UtcDateTime });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<IReadOnlyList<PriceSummary>> ListTenantPricesAsync(TenantId id, CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT model, effective_from, input_micro_usd_per_million, output_micro_usd_per_million
            FROM tenant_model_prices WHERE tenant_id = @id ORDER BY model, effective_from DESC
            """);
        command.Parameters.AddWithValue("id", id.Value);

        var prices = new List<PriceSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            prices.Add(new PriceSummary(
                reader.GetString(0),
                new ModelPrice(Money.FromMicroDollars(reader.GetInt64(2)), Money.FromMicroDollars(reader.GetInt64(3))),
                AsUtc(reader.GetDateTime(1))));
        }

        return prices;
    }

    public async ValueTask<bool> SetTenantPriceAsync(TenantId id, string model, ModelPrice price, DateTimeOffset effectiveFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(price);

        // Inserting from the tenants table makes an unknown tenant insert nothing, instead of failing on the foreign key.
        await using var command = Command("""
            INSERT INTO tenant_model_prices (tenant_id, model, effective_from, input_micro_usd_per_million, output_micro_usd_per_million)
            SELECT t.id, @model, @from, @input, @output FROM tenants t WHERE t.id = @id
            ON CONFLICT (tenant_id, model, effective_from) DO UPDATE
            SET input_micro_usd_per_million = EXCLUDED.input_micro_usd_per_million,
                output_micro_usd_per_million = EXCLUDED.output_micro_usd_per_million
            """);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("model", model);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = effectiveFrom.UtcDateTime });
        command.Parameters.AddWithValue("input", price.InputPerMillionTokens.MicroDollars);
        command.Parameters.AddWithValue("output", price.OutputPerMillionTokens.MicroDollars);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask<int> RemoveTenantPricesAsync(TenantId id, string model, CancellationToken cancellationToken)
    {
        await using var command = Command("DELETE FROM tenant_model_prices WHERE tenant_id = @id AND model = @model");
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("model", model);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<PriceSummary>> ListPricesAsync(CancellationToken cancellationToken)
    {
        await using var command = Command("""
            SELECT model, effective_from, input_micro_usd_per_million, output_micro_usd_per_million
            FROM model_prices ORDER BY model, effective_from DESC
            """);

        var prices = new List<PriceSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            prices.Add(new PriceSummary(
                reader.GetString(0),
                new ModelPrice(Money.FromMicroDollars(reader.GetInt64(2)), Money.FromMicroDollars(reader.GetInt64(3))),
                AsUtc(reader.GetDateTime(1))));
        }

        return prices;
    }

    public async ValueTask SetPriceAsync(string model, ModelPrice price, DateTimeOffset effectiveFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(price);

        await using var command = Command("""
            INSERT INTO model_prices (model, effective_from, input_micro_usd_per_million, output_micro_usd_per_million)
            VALUES (@model, @from, @input, @output)
            ON CONFLICT (model, effective_from) DO UPDATE
            SET input_micro_usd_per_million = EXCLUDED.input_micro_usd_per_million,
                output_micro_usd_per_million = EXCLUDED.output_micro_usd_per_million
            """);
        command.Parameters.AddWithValue("model", model);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = effectiveFrom.UtcDateTime });
        command.Parameters.AddWithValue("input", price.InputPerMillionTokens.MicroDollars);
        command.Parameters.AddWithValue("output", price.OutputPerMillionTokens.MicroDollars);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private static TenantSummary ReadTenant(NpgsqlDataReader reader) =>
        new(
            TenantId.Parse(reader.GetString(0)),
            reader.GetString(1),
            new BudgetPolicy(Money.FromMicroDollars(reader.GetInt64(2)), TimeSpan.FromSeconds(reader.GetInt32(3))),
            reader.GetBoolean(4),
            AsUtc(reader.GetDateTime(5)),
            AsUtc(reader.GetDateTime(6)))
        {
            PriceMultiplierBasisPoints = reader.GetInt32(7),
        };

    private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private NpgsqlCommand Command(string sql) =>
        _dataSource.CreateCommand(sql).WithTimeout(_timeoutSeconds);
}

internal static class NpgsqlCommandExtensions
{
    public static NpgsqlCommand WithTimeout(this NpgsqlCommand command, int seconds)
    {
        command.CommandTimeout = seconds;
        return command;
    }
}
