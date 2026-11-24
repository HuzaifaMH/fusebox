using System.Data;
using Fusebox.Domain;
using Fusebox.Infrastructure.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fusebox.Infrastructure.Catalog.Postgres;

/// <summary>
/// Reads tenants and prices from PostgreSQL, the source of truth. Reads one consistent snapshot of both tables.
/// A row that cannot become a valid tenant or price is skipped and logged, so one bad row cannot take every tenant down.
/// </summary>
internal sealed partial class PostgresCatalogSource : ICatalogSource, IAsyncDisposable
{
    private const string SchemaResourcePrefix = "Fusebox.Infrastructure.Catalog.Postgres.Schema.";

    // Any constant will do; every gateway instance uses the same one, so only one at a time creates the tables.
    private const long SchemaLockKey = 7_461_001;

    private const string TenantsSql = """
        SELECT id, name, api_key_hash, budget_limit_micro_usd, budget_window_seconds, price_multiplier_bp
        FROM tenants
        WHERE enabled
        ORDER BY id
        """;

    // The newest price per model that is already in effect.
    private const string PricesSql = """
        SELECT DISTINCT ON (model) model, input_micro_usd_per_million, output_micro_usd_per_million
        FROM model_prices
        WHERE effective_from <= now()
        ORDER BY model, effective_from DESC
        """;

    // The newest negotiated rate per tenant and model that is already in effect.
    private const string TenantPricesSql = """
        SELECT DISTINCT ON (tenant_id, model) tenant_id, model, input_micro_usd_per_million, output_micro_usd_per_million
        FROM tenant_model_prices
        WHERE effective_from <= now()
        ORDER BY tenant_id, model, effective_from DESC
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOptions _options;
    private readonly ILogger<PostgresCatalogSource> _logger;

    public PostgresCatalogSource(IOptions<CatalogSourceOptions> options, ILogger<PostgresCatalogSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value.Postgres;
        _logger = logger;
        _dataSource = NpgsqlDataSource.Create(_options.ConnectionString);
    }

    public async ValueTask PrepareAsync(CancellationToken cancellationToken)
    {
        if (_options.ApplySchemaOnStartup)
        {
            await PostgresSchema.ApplyAsync(_dataSource, SchemaResourcePrefix, SchemaLockKey, _options.CommandTimeout, cancellationToken);
        }
    }

    public async ValueTask<CatalogData> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        // One snapshot for both queries, so a change made between them cannot give a tenant and a price that never coexisted.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var tenantPrices = await ReadTenantPricesAsync(connection, transaction, cancellationToken);
        var tenants = await ReadTenantsAsync(connection, transaction, tenantPrices, cancellationToken);
        var prices = await ReadPricesAsync(connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CatalogData(tenants, prices);
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private async Task<List<TenantEntry>> ReadTenantsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Dictionary<string, Dictionary<string, ModelPrice>> tenantPrices,
        CancellationToken cancellationToken)
    {
        var tenants = new List<TenantEntry>();

        await using var command = CreateCommand(connection, transaction, TenantsSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);

            try
            {
                tenants.Add(new TenantEntry(
                    reader.GetString(2),
                    new Tenant(
                        TenantId.Parse(id),
                        reader.GetString(1),
                        new BudgetPolicy(Money.FromMicroDollars(reader.GetInt64(3)), TimeSpan.FromSeconds(reader.GetInt32(4))))
                    {
                        Pricing = new TenantPricing(reader.GetInt32(5), tenantPrices.GetValueOrDefault(id)),
                    }));
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                LogInvalidTenant(exception, id);
            }
        }

        return tenants;
    }

    // A bad row costs that one negotiated rate, which then falls back to list price; it never takes a tenant down.
    private async Task<Dictionary<string, Dictionary<string, ModelPrice>>> ReadTenantPricesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Dictionary<string, ModelPrice>>(StringComparer.Ordinal);

        await using var command = CreateCommand(connection, transaction, TenantPricesSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var tenantId = reader.GetString(0);
            var model = reader.GetString(1);

            if (!result.TryGetValue(tenantId, out var prices))
            {
                result[tenantId] = prices = new Dictionary<string, ModelPrice>(StringComparer.Ordinal);
            }

            prices[model] = new ModelPrice(Money.FromMicroDollars(reader.GetInt64(2)), Money.FromMicroDollars(reader.GetInt64(3)));
        }

        return result;
    }

    private async Task<List<PriceEntry>> ReadPricesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var prices = new List<PriceEntry>();

        await using var command = CreateCommand(connection, transaction, PricesSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var model = reader.GetString(0);

            try
            {
                prices.Add(new PriceEntry(
                    model,
                    new ModelPrice(Money.FromMicroDollars(reader.GetInt64(1)), Money.FromMicroDollars(reader.GetInt64(2)))));
            }
            catch (ArgumentException exception)
            {
                LogInvalidPrice(exception, model);
            }
        }

        return prices;
    }

    private NpgsqlCommand CreateCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = (int)_options.CommandTimeout.TotalSeconds };

    [LoggerMessage(EventId = 4101, Level = LogLevel.Error,
        Message = "Skipped tenant {TenantId}: its row is not a valid tenant")]
    private partial void LogInvalidTenant(Exception exception, string tenantId);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Error,
        Message = "Skipped the price of model {Model}: its row is not a valid price")]
    private partial void LogInvalidPrice(Exception exception, string model);
}
