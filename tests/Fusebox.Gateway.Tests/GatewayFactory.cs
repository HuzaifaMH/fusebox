using System.Security.Cryptography;
using System.Text;
using Fusebox.Application.Telemetry;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Time.Testing;

namespace Fusebox.Gateway.Tests;

/// <summary>
/// Hosts the real gateway in memory, pointed at a <see cref="FakeUpstream"/>, with one tenant and one priced model.
/// </summary>
internal sealed class GatewayFactory(
    FakeUpstream upstream,
    decimal budgetUsd = 1.00m,
    IReadOnlyDictionary<string, string?>? settings = null) : WebApplicationFactory<Program>
{
    public const string TenantKey = "acme-test-key";
    public const string VendorKey = "vendor-secret-key";
    public const string Model = "test-model";

    /// <summary>Pinned so budget windows never straddle a boundary mid-test.</summary>
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-03-04T10:15:00Z"));

    /// <summary>Catches the record of every request, in place of the real queue.</summary>
    public RecordingTelemetrySink Telemetry { get; } = new();

    public static string HashOf(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:openai:Destinations:primary:Address"] = upstream.Address.ToString(),

            // A route that is deliberately not metered, and one with no metering decision at all.
            ["ReverseProxy:Routes:openai-models:ClusterId"] = "openai",
            ["ReverseProxy:Routes:openai-models:Match:Path"] = "/openai/v1/models",
            ["ReverseProxy:Routes:openai-models:Transforms:0:PathRemovePrefix"] = "/openai",
            ["ReverseProxy:Routes:openai-models:Metadata:Fusebox.Metering"] = "none",
            ["ReverseProxy:Routes:undecided:ClusterId"] = "openai",
            ["ReverseProxy:Routes:undecided:Match:Path"] = "/undecided/v1/chat/completions",
            ["ReverseProxy:Routes:undecided:Transforms:0:PathRemovePrefix"] = "/undecided",

            // A metered route that must never serve unverified, and one with a failure mode nobody understands.
            ["ReverseProxy:Routes:strict:ClusterId"] = "openai",
            ["ReverseProxy:Routes:strict:Match:Path"] = "/strict/v1/chat/completions",
            ["ReverseProxy:Routes:strict:Transforms:0:PathRemovePrefix"] = "/strict",
            ["ReverseProxy:Routes:strict:Metadata:Fusebox.Protocol"] = "openai-chat",
            ["ReverseProxy:Routes:strict:Metadata:Fusebox.FailureMode"] = "FailClosed",
            ["ReverseProxy:Routes:badmode:ClusterId"] = "openai",
            ["ReverseProxy:Routes:badmode:Match:Path"] = "/badmode/v1/chat/completions",
            ["ReverseProxy:Routes:badmode:Transforms:0:PathRemovePrefix"] = "/badmode",
            ["ReverseProxy:Routes:badmode:Metadata:Fusebox.Protocol"] = "openai-chat",
            ["ReverseProxy:Routes:badmode:Metadata:Fusebox.FailureMode"] = "Maybe",

            ["Fusebox:Upstreams:Keys:openai"] = VendorKey,

            // $1,000 per million input tokens and $2,000 per million output tokens, so a token costs $0.001 or $0.002.
            ["Fusebox:Catalog:Models:test-model:InputUsdPerMillionTokens"] = "1000",
            ["Fusebox:Catalog:Models:test-model:OutputUsdPerMillionTokens"] = "2000",

            ["Fusebox:Catalog:Tenants:acme:Name"] = "Acme",
            ["Fusebox:Catalog:Tenants:acme:ApiKeyHash"] = HashOf(TenantKey),
            ["Fusebox:Catalog:Tenants:acme:BudgetLimitUsd"] = budgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Fusebox:Catalog:Tenants:acme:BudgetWindow"] = "01:00:00",

            ["Fusebox:Gateway:MaxRequestBodyBytes"] = "4096",
        }));

        // Added after the defaults above, so a test can override any of them, such as pointing the budget store at a dead Redis.
        if (settings is not null)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<ITelemetrySink>(Telemetry);
        });
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }
}
