using Fusebox.Domain;
using Fusebox.Infrastructure.Catalog;
using Fusebox.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Tests;

internal sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public class Sha256ApiKeyHasherTests
{
    private readonly Sha256ApiKeyHasher _hasher = new();

    [Fact]
    public void Produces_the_known_lowercase_hex_digest()
    {
        // The development tenant in appsettings.Development.json stores this exact hash.
        Assert.Equal(
            "a51d6460413535f03589aa5ed0b4e8ce983d5e8c2f10cd2194fbfc828965df3c",
            _hasher.Hash("fbx_dev_demo_key"));
    }

    [Fact]
    public void Different_keys_give_different_hashes()
    {
        Assert.NotEqual(_hasher.Hash("key-a"), _hasher.Hash("key-b"));
    }
}

public class CatalogOptionsValidatorTests
{
    private static readonly string ValidHash = new('a', 64);
    private readonly CatalogOptionsValidator _validator = new();

    private static TenantOptions ValidTenant(string hash) => new()
    {
        Name = "Acme",
        ApiKeyHash = hash,
        BudgetLimitUsd = 10m,
        BudgetWindow = TimeSpan.FromHours(1),
    };

    private ValidateOptionsResult Validate(CatalogOptions options) => _validator.Validate(null, options);

    [Fact]
    public void A_valid_catalog_passes()
    {
        var result = Validate(new CatalogOptions
        {
            Tenants = { ["acme"] = ValidTenant(ValidHash) },
            Models = { ["m"] = new ModelPriceOptions { InputUsdPerMillionTokens = 1, OutputUsdPerMillionTokens = 2 } },
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void An_empty_catalog_passes()
    {
        Assert.True(Validate(new CatalogOptions()).Succeeded);
    }

    [Fact]
    public void An_invalid_tenant_id_fails()
    {
        var result = Validate(new CatalogOptions { Tenants = { ["bad id!"] = ValidTenant(ValidHash) } });

        Assert.True(result.Failed);
        Assert.Contains("bad id!", string.Join(' ', result.Failures!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tooshort")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void A_hash_that_is_not_lowercase_hex_sha256_fails(string hash)
    {
        Assert.True(Validate(new CatalogOptions { Tenants = { ["acme"] = ValidTenant(hash) } }).Failed);
    }

    [Fact]
    public void Two_tenants_sharing_a_key_hash_fail()
    {
        var result = Validate(new CatalogOptions
        {
            Tenants = { ["one"] = ValidTenant(ValidHash), ["two"] = ValidTenant(ValidHash) },
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void A_budget_of_zero_fails()
    {
        var tenant = ValidTenant(ValidHash);
        tenant.BudgetLimitUsd = 0;

        Assert.True(Validate(new CatalogOptions { Tenants = { ["acme"] = tenant } }).Failed);
    }

    [Fact]
    public void A_window_under_a_minute_fails()
    {
        var tenant = ValidTenant(ValidHash);
        tenant.BudgetWindow = TimeSpan.FromSeconds(30);

        Assert.True(Validate(new CatalogOptions { Tenants = { ["acme"] = tenant } }).Failed);
    }

    [Fact]
    public void A_negative_model_price_fails()
    {
        var result = Validate(new CatalogOptions
        {
            Models = { ["m"] = new ModelPriceOptions { InputUsdPerMillionTokens = -1 } },
        });

        Assert.True(result.Failed);
    }
}

public class EnforcementOptionsValidatorTests
{
    private readonly Enforcement.EnforcementOptionsValidator _validator = new();

    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.True(_validator.Validate(null, new Enforcement.EnforcementOptions()).Succeeded);
    }

    [Fact]
    public void Redis_needs_a_connection_string()
    {
        var result = _validator.Validate(null, new Enforcement.EnforcementOptions { Store = Enforcement.EnforcementStoreKind.Redis });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Redis_with_a_connection_string_is_valid()
    {
        var options = new Enforcement.EnforcementOptions
        {
            Store = Enforcement.EnforcementStoreKind.Redis,
            Redis = new Enforcement.RedisOptions { ConnectionString = "localhost:6379" },
        };

        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Fact]
    public void A_key_prefix_with_a_brace_is_rejected_because_it_would_break_cluster_hash_tags()
    {
        var options = new Enforcement.EnforcementOptions
        {
            Store = Enforcement.EnforcementStoreKind.Redis,
            Redis = new Enforcement.RedisOptions { ConnectionString = "localhost", KeyPrefix = "bad{prefix" },
        };

        Assert.True(_validator.Validate(null, options).Failed);
    }
}
