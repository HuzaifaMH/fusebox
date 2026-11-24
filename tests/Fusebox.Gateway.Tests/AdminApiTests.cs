using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fusebox.Application.Admin;
using Fusebox.Application.Alerting;
using Fusebox.Application.Reconciliation;
using Fusebox.Domain;
using Fusebox.Gateway.Admin;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Fusebox.Gateway.Tests;

/// <summary>The admin API through the real host, with the database replaced by an in-memory store.</summary>
public sealed class AdminApiTests : IAsyncLifetime
{
    private const string AdminKey = "admin-test-key";

    private readonly InMemoryAdminStore _store = new();
    private readonly RecordingReload _reload = new();
    private readonly InMemoryAlerts _alerts = new();
    private readonly StubUsage _usage = new();
    private FakeUpstream _upstream = null!;
    private GatewayFactory _plain = null!;
    private WebApplicationFactory _admin = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeUpstream.StartAsync();

        _plain = new GatewayFactory(_upstream);
        _admin = new WebApplicationFactory(_plain.WithWebHostBuilder(host => host
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fusebox:Admin:Enabled"] = "true",
                ["Fusebox:Admin:ApiKeyHash"] = GatewayFactory.HashOf(AdminKey),
            }))
            .ConfigureTestServices(services =>
            {
                // The real validator wants a PostgreSQL catalog; this host runs on the in-memory one.
                services.RemoveAll<IValidateOptions<AdminOptions>>();
                services.RemoveAll<ICatalogAdminStore>();
                services.RemoveAll<ICatalogReload>();
                services.RemoveAll<IAlertStore>();
                services.RemoveAll<IAlertFeed>();
                services.RemoveAll<IUsageCostReader>();
                services.AddSingleton<IUsageCostReader>(_usage);
                services.AddSingleton<ICatalogAdminStore>(_store);
                services.AddSingleton<ICatalogReload>(_reload);
                services.AddSingleton<IAlertStore>(_alerts);
                services.AddSingleton<IAlertFeed>(_alerts);
            })));

        _client = _admin.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _admin.Dispose();
        await _plain.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body = null, string? key = AdminKey)
    {
        var request = new HttpRequestMessage(method, path);

        if (key is not null)
        {
            request.Headers.Add(AdminKeyFilter.HeaderName, key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? key = AdminKey)
    {
        using var request = Request(method, path, body, key);
        return await _client.SendAsync(request);
    }

    private static readonly object Acme = new { id = "acme", name = "Acme Inc", budgetLimitUsd = 25.5m, budgetWindowSeconds = 3600 };

    [Fact]
    public async Task A_request_without_the_admin_key_is_refused()
    {
        using var response = await SendAsync(HttpMethod.Get, "/admin/tenants", key: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_admin_key_is_refused()
    {
        using var response = await SendAsync(HttpMethod.Get, "/admin/tenants", key: "not-the-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_tenant_key_does_not_open_the_admin_api()
    {
        using var response = await SendAsync(HttpMethod.Get, "/admin/tenants", key: GatewayFactory.TenantKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_tenant_returns_its_key_once_and_stores_only_the_hash()
    {
        using var response = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/admin/tenants/acme", response.Headers.Location!.ToString());

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var key = json.RootElement.GetProperty("apiKey").GetString()!;
        Assert.StartsWith("fbx_", key, StringComparison.Ordinal);
        Assert.True(key.Length >= 40);
        Assert.Equal(25.5m, json.RootElement.GetProperty("tenant").GetProperty("budgetLimitUsd").GetDecimal());

        Assert.Equal(GatewayFactory.HashOf(key), _store.KeyHashOf("acme"));
        Assert.DoesNotContain(key, _store.AllStoredText(), StringComparison.Ordinal);
        Assert.Equal(1, _reload.Requests);

        // Reading the tenant back never shows a key.
        using var read = await SendAsync(HttpMethod.Get, "/admin/tenants/acme");
        Assert.DoesNotContain("apiKey", await read.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_tenants_never_get_the_same_key()
    {
        using var first = await SendAsync(HttpMethod.Post, "/admin/tenants", new { id = "one", name = "One", budgetLimitUsd = 1m, budgetWindowSeconds = 3600 });
        using var second = await SendAsync(HttpMethod.Post, "/admin/tenants", new { id = "two", name = "Two", budgetLimitUsd = 1m, budgetWindowSeconds = 3600 });

        Assert.NotEqual(_store.KeyHashOf("one"), _store.KeyHashOf("two"));
    }

    [Fact]
    public async Task Creating_a_tenant_that_exists_is_a_conflict()
    {
        using var first = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);
        using var second = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Theory]
    [InlineData("bad id!", "Acme", 10, 3600)]
    [InlineData("acme", "", 10, 3600)]
    [InlineData("acme", "Acme", 0, 3600)]
    [InlineData("acme", "Acme", -5, 3600)]
    [InlineData("acme", "Acme", 10, 5)]
    public async Task An_invalid_tenant_is_a_bad_request_and_nothing_is_saved(string id, string name, decimal limit, int window)
    {
        using var response = await SendAsync(HttpMethod.Post, "/admin/tenants", new { id, name, budgetLimitUsd = limit, budgetWindowSeconds = window });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_store.Tenants);
        Assert.Equal(0, _reload.Requests);
    }

    [Fact]
    public async Task Changing_a_budget_updates_the_tenant_and_asks_for_a_reload()
    {
        using var created = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);

        using var response = await SendAsync(HttpMethod.Put, "/admin/tenants/acme/budget", new { budgetLimitUsd = 100m, budgetWindowSeconds = 86_400 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var tenant = _store.Tenants["acme"].Summary;
        Assert.Equal(Money.FromDollars(100m), tenant.Budget.Limit);
        Assert.Equal(TimeSpan.FromDays(1), tenant.Budget.Window);
        Assert.Equal(2, _reload.Requests);
    }

    [Fact]
    public async Task Changing_an_unknown_tenant_is_not_found()
    {
        using var response = await SendAsync(HttpMethod.Put, "/admin/tenants/ghost/budget", new { budgetLimitUsd = 1m, budgetWindowSeconds = 3600 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_tenant_can_be_disabled_and_enabled_again()
    {
        using var created = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);

        using var disable = await SendAsync(HttpMethod.Post, "/admin/tenants/acme/disable");
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.False(_store.Tenants["acme"].Summary.Enabled);

        using var enable = await SendAsync(HttpMethod.Post, "/admin/tenants/acme/enable");
        Assert.Equal(HttpStatusCode.NoContent, enable.StatusCode);
        Assert.True(_store.Tenants["acme"].Summary.Enabled);
    }

    [Fact]
    public async Task Rotating_a_key_replaces_the_hash_and_returns_the_new_key_once()
    {
        using var created = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);
        var oldHash = _store.KeyHashOf("acme");

        using var response = await SendAsync(HttpMethod.Post, "/admin/tenants/acme/rotate-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var newKey = json.RootElement.GetProperty("apiKey").GetString()!;
        Assert.NotEqual(oldHash, _store.KeyHashOf("acme"));
        Assert.Equal(GatewayFactory.HashOf(newKey), _store.KeyHashOf("acme"));
    }

    [Fact]
    public async Task A_model_price_can_be_set_now_or_scheduled()
    {
        using var now = await SendAsync(HttpMethod.Put, "/admin/prices/gpt-test", new { inputUsdPerMillionTokens = 2.5m, outputUsdPerMillionTokens = 10m });
        using var later = await SendAsync(
            HttpMethod.Put,
            "/admin/prices/gpt-test",
            new { inputUsdPerMillionTokens = 3m, outputUsdPerMillionTokens = 12m, effectiveFrom = "2030-01-01T00:00:00Z" });

        Assert.Equal(HttpStatusCode.NoContent, now.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, later.StatusCode);

        var prices = await (await SendAsync(HttpMethod.Get, "/admin/prices")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, prices.GetArrayLength());
    }

    [Fact]
    public async Task A_model_name_with_a_slash_is_accepted()
    {
        using var response = await SendAsync(HttpMethod.Put, "/admin/prices/meta/llama-3", new { inputUsdPerMillionTokens = 1m, outputUsdPerMillionTokens = 2m });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(_store.Prices, p => p.Model == "meta/llama-3");
    }

    [Fact]
    public async Task A_negative_price_is_a_bad_request()
    {
        using var response = await SendAsync(HttpMethod.Put, "/admin/prices/gpt-test", new { inputUsdPerMillionTokens = -1m, outputUsdPerMillionTokens = 2m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Open_anomalies_come_with_their_diagnosis()
    {
        _alerts.Anomalies.Add(new AnomalyAlertState(
            Guid.NewGuid(),
            new Fusebox.Application.Detection.SeriesKey(TenantId.Parse("acme"), "openai-chat"),
            Fusebox.Application.Detection.Signal.Spend,
            true,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null,
            200_000,
            Fusebox.Application.Detection.BaselineBasis.Recent,
            1_000_000,
            2_400_000,
            12,
            new DiagnosisSummary("Calls per minute went up.", "Look for a retry loop.", Fusebox.Application.Diagnosis.NarrationConfidence.High, Fusebox.Application.Diagnosis.NarrationSource.Template)));

        var anomalies = await (await SendAsync(HttpMethod.Get, "/admin/anomalies")).Content.ReadFromJsonAsync<JsonElement>();

        var first = anomalies[0];
        Assert.Equal("acme", first.GetProperty("tenantId").GetString());
        Assert.Equal("Spend", first.GetProperty("signal").GetString());
        Assert.Equal("Look for a retry loop.", first.GetProperty("diagnosis").GetProperty("suggestedNextStep").GetString());
    }

    [Fact]
    public async Task Open_alerts_are_listed_most_severe_first()
    {
        var now = DateTimeOffset.UtcNow;
        _alerts.Open.Add(new AlertRecord(Guid.NewGuid(), "a", AlertKind.Anomaly, AlertSeverity.Warning, "Slow", "s", new Dictionary<string, string>(), "r", now, now));
        _alerts.Open.Add(new AlertRecord(Guid.NewGuid(), "b", AlertKind.BudgetExhausted, AlertSeverity.Critical, "Over", "s", new Dictionary<string, string>(), "r", now, now));

        var alerts = await (await SendAsync(HttpMethod.Get, "/admin/alerts")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Critical", alerts[0].GetProperty("severity").GetString());
        Assert.Equal("Warning", alerts[1].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task The_api_description_and_swagger_ui_are_served()
    {
        using var description = await _client.GetAsync("/admin/openapi/admin.json");
        using var ui = await _client.GetAsync("/admin/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, description.StatusCode);
        var text = await description.Content.ReadAsStringAsync();
        Assert.Contains("/admin/tenants", text, StringComparison.Ordinal);
        Assert.Contains("rotate-key", text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }

    [Fact]
    public async Task The_admin_routes_do_not_exist_unless_switched_on()
    {
        using var client = _plain.CreateClient();

        using var response = await client.GetAsync("/admin/tenants");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Switching_the_admin_api_on_needs_a_key_hash_and_a_postgres_catalog()
    {
        var postgres = Options.Create(new Fusebox.Infrastructure.Catalog.CatalogSourceOptions { Kind = Fusebox.Infrastructure.Catalog.CatalogSourceKind.Postgres });
        var configuration = Options.Create(new Fusebox.Infrastructure.Catalog.CatalogSourceOptions());

        Assert.True(new AdminOptionsValidator(postgres).Validate(null, new AdminOptions { Enabled = true, ApiKeyHash = "nope" }).Failed);
        Assert.True(new AdminOptionsValidator(configuration).Validate(null, new AdminOptions { Enabled = true, ApiKeyHash = new string('a', 64) }).Failed);
        Assert.True(new AdminOptionsValidator(postgres).Validate(null, new AdminOptions { Enabled = true, ApiKeyHash = new string('a', 64) }).Succeeded);
        Assert.True(new AdminOptionsValidator(configuration).Validate(null, new AdminOptions()).Succeeded);
    }

    [Fact]
    public async Task A_price_multiplier_can_be_set_and_is_shown_on_the_tenant()
    {
        using var created = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);

        using var response = await SendAsync(HttpMethod.Put, "/admin/tenants/acme/pricing", new { priceMultiplier = 0.85m });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(8_500, _store.Tenants["acme"].Summary.PriceMultiplierBasisPoints);
        Assert.Equal(2, _reload.Requests);

        var tenant = await (await SendAsync(HttpMethod.Get, "/admin/tenants/acme")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0.85m, tenant.GetProperty("priceMultiplier").GetDecimal());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(0.12345)]
    public async Task A_multiplier_that_cannot_be_right_is_a_bad_request(double multiplier)
    {
        using var created = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);

        using var response = await SendAsync(HttpMethod.Put, "/admin/tenants/acme/pricing", new { priceMultiplier = multiplier });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(10_000, _store.Tenants["acme"].Summary.PriceMultiplierBasisPoints);
    }

    [Fact]
    public async Task A_negotiated_rate_can_be_set_listed_and_removed()
    {
        using var created = await SendAsync(HttpMethod.Post, "/admin/tenants", Acme);

        using var set = await SendAsync(HttpMethod.Put, "/admin/tenants/acme/prices/meta/llama-3", new { inputUsdPerMillionTokens = 1m, outputUsdPerMillionTokens = 2m });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        var listed = await (await SendAsync(HttpMethod.Get, "/admin/tenants/acme/prices")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("meta/llama-3", listed[0].GetProperty("model").GetString());

        using var removed = await SendAsync(HttpMethod.Delete, "/admin/tenants/acme/prices/meta/llama-3");
        using var removedAgain = await SendAsync(HttpMethod.Delete, "/admin/tenants/acme/prices/meta/llama-3");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedAgain.StatusCode);
    }

    [Fact]
    public async Task Negotiated_rates_for_an_unknown_tenant_are_not_found()
    {
        using var set = await SendAsync(HttpMethod.Put, "/admin/tenants/ghost/prices/m", new { inputUsdPerMillionTokens = 1m, outputUsdPerMillionTokens = 2m });
        using var list = await SendAsync(HttpMethod.Get, "/admin/tenants/ghost/prices");

        Assert.Equal(HttpStatusCode.NotFound, set.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }

    [Fact]
    public async Task Reconciliation_reports_where_fusebox_and_the_vendor_differ()
    {
        _usage.Costs["gpt-a"] = Money.FromDollars(97m);

        using var response = await SendAsync(HttpMethod.Post, "/admin/reconciliation", new
        {
            periodStart = "2026-01-01T00:00:00Z",
            periodEnd = "2026-02-01T00:00:00Z",
            lines = new[] { new { model = "gpt-a", vendorCostUsd = 100m } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FuseboxUnder", report.GetProperty("total").GetProperty("status").GetString());
        Assert.Equal(-0.03, report.GetProperty("total").GetProperty("drift").GetDouble(), precision: 6);
        Assert.Equal("gpt-a", report.GetProperty("models")[0].GetProperty("model").GetString());
        Assert.True(report.GetProperty("total").GetProperty("suggestedPriceFactor").GetDouble() > 1);
    }

    [Fact]
    public async Task A_statement_that_cannot_be_compared_is_a_bad_request()
    {
        using var offTheHour = await SendAsync(HttpMethod.Post, "/admin/reconciliation", new
        {
            periodStart = "2026-01-01T00:30:00Z",
            periodEnd = "2026-02-01T00:00:00Z",
            lines = new[] { new { model = (string?)null, vendorCostUsd = 100m } },
        });
        using var tooRecent = await SendAsync(HttpMethod.Post, "/admin/reconciliation", new
        {
            periodStart = "2026-01-01T00:00:00Z",
            periodEnd = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-dd'T'HH':00:00Z'", System.Globalization.CultureInfo.InvariantCulture),
            lines = new[] { new { model = (string?)null, vendorCostUsd = 100m } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, offTheHour.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooRecent.StatusCode);
    }

    [Fact]
    public async Task Reconciliation_needs_the_admin_key()
    {
        using var response = await SendAsync(HttpMethod.Post, "/admin/reconciliation", new { }, key: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class StubUsage : IUsageCostReader
    {
        public Dictionary<string, Money> Costs { get; } = [];

        public ValueTask<IReadOnlyDictionary<string, Money>> CostByModelAsync(DateTimeOffset periodStart, DateTimeOffset periodEnd, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<string, Money>>(Costs);
    }

    // The factory returned by WithWebHostBuilder is the same type; this wrapper only gives it a name that reads well here.
    private sealed class WebApplicationFactory(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> inner) : IDisposable
    {
        public HttpClient CreateClient() => inner.CreateClient();

        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingReload : ICatalogReload
    {
        public int Requests { get; private set; }

        public void RequestReload() => Requests++;
    }

    private sealed class InMemoryAlerts : IAlertStore, IAlertFeed
    {
        public List<AnomalyAlertState> Anomalies { get; } = [];

        public List<AlertRecord> Open { get; } = [];

        public ValueTask<IReadOnlyList<AnomalyAlertState>> LoadAnomaliesAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AnomalyAlertState>>([.. Anomalies]);

        public ValueTask<IReadOnlyList<AlertRecord>> FindOpenByKindAsync(AlertKind kind, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AlertRecord>>([.. Open.Where(a => a.Kind == kind)]);

        public ValueTask<IReadOnlyList<BudgetBlock>> LoadBudgetBlocksAsync(DateTimeOffset since, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<BudgetBlock>>([]);

        public ValueTask<bool> TryRunExclusivelyAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<AlertRecord?> FindOpenAsync(string fingerprint, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DateTimeOffset?> FindLastResolvedAsync(string fingerprint, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> TryOpenAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask UpdateAsync(AlertRecord alert, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ResolveAsync(Guid alertId, DateTimeOffset at, IReadOnlyList<DeliveryRequest> deliveries, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<PendingDelivery>> ClaimDueAsync(DateTimeOffset now, int limit, TimeSpan claimFor, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask MarkSentAsync(Guid deliveryId, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask ScheduleRetryAsync(Guid deliveryId, DateTimeOffset nextAttemptAt, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask MarkFailedAsync(Guid deliveryId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class InMemoryAdminStore : ICatalogAdminStore
    {
        public Dictionary<string, (TenantSummary Summary, string KeyHash)> Tenants { get; } = [];

        public List<PriceSummary> Prices { get; } = [];

        public string KeyHashOf(string id) => Tenants[id].KeyHash;

        public string AllStoredText() => string.Join('\n', Tenants.Values.Select(t => $"{t.Summary} {t.KeyHash}"));

        public ValueTask<IReadOnlyList<TenantSummary>> ListTenantsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<TenantSummary>>([.. Tenants.Values.Select(t => t.Summary)]);

        public ValueTask<TenantSummary?> FindTenantAsync(TenantId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Tenants.TryGetValue(id.Value, out var t) ? t.Summary : null);

        public ValueTask<bool> TryAddTenantAsync(NewTenant tenant, DateTimeOffset at, CancellationToken cancellationToken)
        {
            if (Tenants.ContainsKey(tenant.Tenant.Id.Value))
            {
                return ValueTask.FromResult(false);
            }

            Tenants[tenant.Tenant.Id.Value] = (new TenantSummary(tenant.Tenant.Id, tenant.Tenant.Name, tenant.Tenant.Budget, true, at, at), tenant.ApiKeyHash);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> SetBudgetAsync(TenantId id, BudgetPolicy budget, DateTimeOffset at, CancellationToken cancellationToken) =>
            Change(id, t => (t.Summary with { Budget = budget, UpdatedAt = at }, t.KeyHash));

        public ValueTask<bool> SetEnabledAsync(TenantId id, bool enabled, DateTimeOffset at, CancellationToken cancellationToken) =>
            Change(id, t => (t.Summary with { Enabled = enabled, UpdatedAt = at }, t.KeyHash));

        public ValueTask<bool> ReplaceKeyHashAsync(TenantId id, string apiKeyHash, DateTimeOffset at, CancellationToken cancellationToken) =>
            Change(id, t => (t.Summary with { UpdatedAt = at }, apiKeyHash));

        public List<(string Tenant, PriceSummary Price)> TenantPrices { get; } = [];

        public ValueTask<bool> SetPriceMultiplierAsync(TenantId id, int basisPoints, DateTimeOffset at, CancellationToken cancellationToken) =>
            Change(id, t => (t.Summary with { PriceMultiplierBasisPoints = basisPoints, UpdatedAt = at }, t.KeyHash));

        public ValueTask<IReadOnlyList<PriceSummary>> ListTenantPricesAsync(TenantId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PriceSummary>>([.. TenantPrices.Where(p => p.Tenant == id.Value).Select(p => p.Price)]);

        public ValueTask<bool> SetTenantPriceAsync(TenantId id, string model, ModelPrice price, DateTimeOffset effectiveFrom, CancellationToken cancellationToken)
        {
            if (!Tenants.ContainsKey(id.Value))
            {
                return ValueTask.FromResult(false);
            }

            TenantPrices.RemoveAll(p => p.Tenant == id.Value && p.Price.Model == model && p.Price.EffectiveFrom == effectiveFrom);
            TenantPrices.Add((id.Value, new PriceSummary(model, price, effectiveFrom)));
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> RemoveTenantPricesAsync(TenantId id, string model, CancellationToken cancellationToken) =>
            ValueTask.FromResult(TenantPrices.RemoveAll(p => p.Tenant == id.Value && p.Price.Model == model));

        public ValueTask<IReadOnlyList<PriceSummary>> ListPricesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PriceSummary>>([.. Prices]);

        public ValueTask SetPriceAsync(string model, ModelPrice price, DateTimeOffset effectiveFrom, CancellationToken cancellationToken)
        {
            Prices.RemoveAll(p => p.Model == model && p.EffectiveFrom == effectiveFrom);
            Prices.Add(new PriceSummary(model, price, effectiveFrom));
            return ValueTask.CompletedTask;
        }

        private ValueTask<bool> Change(TenantId id, Func<(TenantSummary Summary, string KeyHash), (TenantSummary, string)> change)
        {
            if (!Tenants.TryGetValue(id.Value, out var current))
            {
                return ValueTask.FromResult(false);
            }

            Tenants[id.Value] = change(current);
            return ValueTask.FromResult(true);
        }
    }
}
