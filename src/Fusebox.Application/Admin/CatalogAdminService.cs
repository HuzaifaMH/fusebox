using System.Security.Cryptography;
using Fusebox.Application.Tenancy;
using Fusebox.Domain;
using Microsoft.Extensions.Logging;

namespace Fusebox.Application.Admin;

/// <summary>A tenant with that id already exists.</summary>
public sealed class AdminConflictException(string message) : Exception(message);

/// <summary>There is no such tenant.</summary>
public sealed class AdminNotFoundException(string message) : Exception(message);

/// <summary>
/// Manages tenants, budgets, keys and prices. Every change is logged as an audit line, but never with a key, and is followed
/// by a request to reload the catalog so this gateway serves it within seconds. Other instances pick it up from the shared cache.
/// </summary>
/// <remarks>Invalid input throws <see cref="ArgumentException"/>, with a message that is safe to show the caller.</remarks>
public sealed partial class CatalogAdminService
{
    private const int KeyBytes = 32;
    private const int MaxModelLength = 128;

    private readonly ICatalogAdminStore _store;
    private readonly IApiKeyHasher _hasher;
    private readonly ICatalogReload _reload;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogAdminService> _logger;

    public CatalogAdminService(
        ICatalogAdminStore store,
        IApiKeyHasher hasher,
        ICatalogReload reload,
        TimeProvider time,
        ILogger<CatalogAdminService> logger)
    {
        _store = store;
        _hasher = hasher;
        _reload = reload;
        _time = time;
        _logger = logger;
    }

    public ValueTask<IReadOnlyList<TenantSummary>> ListTenantsAsync(CancellationToken cancellationToken) => _store.ListTenantsAsync(cancellationToken);

    public ValueTask<IReadOnlyList<PriceSummary>> ListPricesAsync(CancellationToken cancellationToken) => _store.ListPricesAsync(cancellationToken);

    public async ValueTask<TenantSummary> GetTenantAsync(string id, CancellationToken cancellationToken) =>
        await _store.FindTenantAsync(TenantId.Parse(id), cancellationToken) ?? throw NotFound(id);

    /// <exception cref="AdminConflictException">The id is taken.</exception>
    public async ValueTask<CreatedTenant> CreateTenantAsync(string id, string name, Money limit, TimeSpan window, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);

        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new ArgumentException("A tenant needs a name of up to 200 characters.", nameof(name));
        }

        if (limit.MicroDollars <= 0)
        {
            throw new ArgumentException("The budget must be more than zero.", nameof(limit));
        }

        var budget = new BudgetPolicy(limit, window);
        var key = NewKey();
        var now = _time.GetUtcNow();

        if (!await _store.TryAddTenantAsync(new NewTenant(new Tenant(tenantId, name.Trim(), budget), _hasher.Hash(key)), now, cancellationToken))
        {
            throw new AdminConflictException($"A tenant named '{tenantId.Value}' already exists.");
        }

        var limitUsd = limit.ToDollars();
        LogTenantCreated(tenantId, limitUsd, (long)window.TotalSeconds);
        _reload.RequestReload();

        var created = await _store.FindTenantAsync(tenantId, cancellationToken)
            ?? new TenantSummary(tenantId, name.Trim(), budget, Enabled: true, now, now);
        return new CreatedTenant(created, key);
    }

    /// <exception cref="AdminNotFoundException">There is no such tenant.</exception>
    public async ValueTask SetBudgetAsync(string id, Money limit, TimeSpan window, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);

        if (limit.MicroDollars <= 0)
        {
            throw new ArgumentException("The budget must be more than zero.", nameof(limit));
        }

        if (!await _store.SetBudgetAsync(tenantId, new BudgetPolicy(limit, window), _time.GetUtcNow(), cancellationToken))
        {
            throw NotFound(id);
        }

        var limitUsd = limit.ToDollars();
        LogBudgetChanged(tenantId, limitUsd, (long)window.TotalSeconds);
        _reload.RequestReload();
    }

    /// <exception cref="AdminNotFoundException">There is no such tenant.</exception>
    public async ValueTask SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);

        if (!await _store.SetEnabledAsync(tenantId, enabled, _time.GetUtcNow(), cancellationToken))
        {
            throw NotFound(id);
        }

        LogEnabledChanged(tenantId, enabled);
        _reload.RequestReload();
    }

    /// <exception cref="AdminNotFoundException">There is no such tenant.</exception>
    public async ValueTask<RotatedKey> RotateKeyAsync(string id, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);
        var key = NewKey();

        if (!await _store.ReplaceKeyHashAsync(tenantId, _hasher.Hash(key), _time.GetUtcNow(), cancellationToken))
        {
            throw NotFound(id);
        }

        LogKeyRotated(tenantId);
        _reload.RequestReload();
        return new RotatedKey(tenantId, key);
    }

    /// <param name="multiplier">What the tenant pays compared with list price: 0.85 is a 15% discount, 1 is list price.</param>
    /// <exception cref="AdminNotFoundException">There is no such tenant.</exception>
    public async ValueTask SetPriceMultiplierAsync(string id, decimal multiplier, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);
        var basisPoints = PriceMultiplier.ToBasisPoints(multiplier);

        if (!await _store.SetPriceMultiplierAsync(tenantId, basisPoints, _time.GetUtcNow(), cancellationToken))
        {
            throw NotFound(id);
        }

        LogMultiplierChanged(tenantId, multiplier);
        _reload.RequestReload();
    }

    /// <exception cref="AdminNotFoundException">There is no such tenant.</exception>
    public async ValueTask<IReadOnlyList<PriceSummary>> ListTenantPricesAsync(string id, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);

        if (await _store.FindTenantAsync(tenantId, cancellationToken) is null)
        {
            throw NotFound(id);
        }

        return await _store.ListTenantPricesAsync(tenantId, cancellationToken);
    }

    /// <summary>Gives a tenant its own rate for a model, which replaces the list price and its multiplier for that model.</summary>
    /// <exception cref="AdminNotFoundException">There is no such tenant.</exception>
    public async ValueTask SetTenantPriceAsync(string id, string model, Money inputPerMillion, Money outputPerMillion, DateTimeOffset? effectiveFrom, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);
        var name = ValidModel(model);
        var from = effectiveFrom ?? _time.GetUtcNow();

        if (!await _store.SetTenantPriceAsync(tenantId, name, new ModelPrice(inputPerMillion, outputPerMillion), from, cancellationToken))
        {
            throw NotFound(id);
        }

        var inputUsd = inputPerMillion.ToDollars();
        var outputUsd = outputPerMillion.ToDollars();
        LogTenantPriceSet(tenantId, name, inputUsd, outputUsd, from);
        _reload.RequestReload();
    }

    /// <summary>Takes away a tenant's own rate for a model, so it pays the list price (times its multiplier) again.</summary>
    /// <exception cref="AdminNotFoundException">The tenant has no negotiated rate for that model.</exception>
    public async ValueTask RemoveTenantPriceAsync(string id, string model, CancellationToken cancellationToken)
    {
        var tenantId = TenantId.Parse(id);
        var name = ValidModel(model);

        if (await _store.RemoveTenantPricesAsync(tenantId, name, cancellationToken) == 0)
        {
            throw new AdminNotFoundException($"Tenant '{id}' has no negotiated rate for '{name}'.");
        }

        LogTenantPriceRemoved(tenantId, name);
        _reload.RequestReload();
    }

    public async ValueTask SetPriceAsync(string model, Money inputPerMillion, Money outputPerMillion, DateTimeOffset? effectiveFrom, CancellationToken cancellationToken)
    {
        ValidModel(model);

        var from = effectiveFrom ?? _time.GetUtcNow();
        await _store.SetPriceAsync(model.Trim(), new ModelPrice(inputPerMillion, outputPerMillion), from, cancellationToken);

        var inputUsd = inputPerMillion.ToDollars();
        var outputUsd = outputPerMillion.ToDollars();
        var name = model.Trim();
        LogPriceSet(name, inputUsd, outputUsd, from);
        _reload.RequestReload();
    }

    private static string ValidModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > MaxModelLength || model.Any(char.IsControl))
        {
            throw new ArgumentException($"A model name is 1-{MaxModelLength} characters with no control characters.", nameof(model));
        }

        return model.Trim();
    }

    // 256 random bits, so the key cannot be guessed and a fast hash is enough to store it safely.
    private static string NewKey() => "fbx_" + Base64Url(RandomNumberGenerator.GetBytes(KeyBytes));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static AdminNotFoundException NotFound(string id) => new($"There is no tenant named '{id}'.");

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information, Message = "Admin created tenant {TenantId} with a budget of ${LimitUsd} per {WindowSeconds}s")]
    private partial void LogTenantCreated(TenantId tenantId, decimal limitUsd, long windowSeconds);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Information, Message = "Admin changed the budget of tenant {TenantId} to ${LimitUsd} per {WindowSeconds}s")]
    private partial void LogBudgetChanged(TenantId tenantId, decimal limitUsd, long windowSeconds);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Information, Message = "Admin set tenant {TenantId} enabled to {Enabled}")]
    private partial void LogEnabledChanged(TenantId tenantId, bool enabled);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Information, Message = "Admin replaced the API key of tenant {TenantId}; the old key no longer works")]
    private partial void LogKeyRotated(TenantId tenantId);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Information, Message = "Admin set the price multiplier of tenant {TenantId} to {Multiplier}x list price")]
    private partial void LogMultiplierChanged(TenantId tenantId, decimal multiplier);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Information, Message = "Admin set a negotiated rate for tenant {TenantId} on {Model}: ${InputUsd} in and ${OutputUsd} out per million tokens from {EffectiveFrom:o}")]
    private partial void LogTenantPriceSet(TenantId tenantId, string model, decimal inputUsd, decimal outputUsd, DateTimeOffset effectiveFrom);

    [LoggerMessage(EventId = 9108, Level = LogLevel.Information, Message = "Admin removed the negotiated rate of tenant {TenantId} on {Model}; it pays list price again")]
    private partial void LogTenantPriceRemoved(TenantId tenantId, string model);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Information, Message = "Admin set the price of {Model} to ${InputUsd} in and ${OutputUsd} out per million tokens from {EffectiveFrom:o}")]
    private partial void LogPriceSet(string model, decimal inputUsd, decimal outputUsd, DateTimeOffset effectiveFrom);
}
