using Fusebox.Application.Budgeting;
using Fusebox.Application.Pricing;
using Fusebox.Application.Tenancy;
using Fusebox.Domain;

namespace Fusebox.Application.Tests;

internal sealed class FakePriceCatalog : IPriceCatalog
{
    private readonly Dictionary<string, ModelPrice> _prices = [];

    public FakePriceCatalog With(string model, ModelPrice price)
    {
        _prices[model] = price;
        return this;
    }

    public void Remove(string model) => _prices.Remove(model);

    public ValueTask<ModelPrice?> FindAsync(TenantId tenantId, string model, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_prices.GetValueOrDefault(model));
}

/// <summary>A store that records what it was asked and answers with whatever the test configured.</summary>
internal sealed class FakeEnforcementStore : IEnforcementStore
{
    public List<BudgetCharge> Charges { get; } = [];

    public List<SettleRequest> Settlements { get; } = [];

    /// <summary>Whether the next reserve call is granted.</summary>
    public bool Grant { get; set; } = true;

    /// <summary>The spend reported back by the next reserve call.</summary>
    public Money Spent { get; set; } = Money.Zero;

    public SettleOutcome SettleOutcome { get; set; } = SettleOutcome.Applied;

    public ValueTask<ReserveResult> TryReserveAsync(BudgetCharge charge, CancellationToken cancellationToken)
    {
        Charges.Add(charge);
        var window = BudgetWindow.Containing(charge.At, charge.Policy.Window);
        return ValueTask.FromResult(new ReserveResult(Grant, window, Spent));
    }

    public ValueTask<SettleOutcome> SettleAsync(SettleRequest request, CancellationToken cancellationToken)
    {
        Settlements.Add(request);
        return ValueTask.FromResult(SettleOutcome);
    }
}

internal sealed class FakeTenantDirectory(params (string Hash, Tenant Tenant)[] tenants) : ITenantDirectory
{
    private readonly Dictionary<string, Tenant> _byHash = tenants.ToDictionary(t => t.Hash, t => t.Tenant);

    public List<string> Lookups { get; } = [];

    public ValueTask<Tenant?> FindByApiKeyHashAsync(string apiKeyHash, CancellationToken cancellationToken)
    {
        Lookups.Add(apiKeyHash);
        return ValueTask.FromResult(_byHash.GetValueOrDefault(apiKeyHash));
    }
}

internal sealed class ReversingHasher : IApiKeyHasher
{
    public string Hash(string apiKey) => new(apiKey.Reverse().ToArray());
}
