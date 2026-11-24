using Fusebox.Application.Tenancy;
using Fusebox.Domain;

namespace Fusebox.Application.Tests;

public class TenantAuthenticatorTests
{
    private static readonly Tenant Acme = new(
        TenantId.Parse("acme"),
        "Acme",
        new BudgetPolicy(Money.FromDollars(10m), TimeSpan.FromHours(1)));

    private readonly FakeTenantDirectory _directory;
    private readonly TenantAuthenticator _authenticator;

    public TenantAuthenticatorTests()
    {
        // The fake hasher reverses the key, so the directory is keyed by the reversed text.
        _directory = new FakeTenantDirectory(("terces", Acme));
        _authenticator = new TenantAuthenticator(new ReversingHasher(), _directory);
    }

    [Fact]
    public async Task A_known_key_finds_its_tenant()
    {
        var tenant = await _authenticator.AuthenticateAsync("secret", CancellationToken.None);

        Assert.Equal(Acme, tenant);
    }

    [Fact]
    public async Task The_raw_key_is_never_passed_to_the_directory()
    {
        await _authenticator.AuthenticateAsync("secret", CancellationToken.None);

        Assert.Equal("terces", Assert.Single(_directory.Lookups));
    }

    [Fact]
    public async Task An_unknown_key_finds_nobody()
    {
        Assert.Null(await _authenticator.AuthenticateAsync("wrong", CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_key_finds_nobody_and_skips_the_lookup(string? apiKey)
    {
        Assert.Null(await _authenticator.AuthenticateAsync(apiKey, CancellationToken.None));
        Assert.Empty(_directory.Lookups);
    }
}
