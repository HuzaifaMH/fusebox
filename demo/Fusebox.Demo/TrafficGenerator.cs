using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Fusebox.Demo;

/// <summary>
/// Sets the demo up through Fusebox's admin API, the way a real operator would, then sends a steady stream of calls
/// for each tenant. One tenant runs away partway through, which is what the anomaly detection and alerts are shown on.
/// </summary>
internal sealed partial class TrafficGenerator(
    IHttpClientFactory clients,
    IOptions<DemoOptions> options,
    TimeProvider time,
    ILogger<TrafficGenerator> logger) : BackgroundService
{
    public const string ClientName = "gateway";

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly ILogger<TrafficGenerator> _logger = logger;
    private readonly DemoOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = clients.CreateClient(ClientName);
        client.BaseAddress = new Uri(_options.GatewayUrl);

        try
        {
            await WaitForGatewayAsync(client, stoppingToken);
            var keys = await SeedAsync(client, stoppingToken);
            await WaitForKeysAsync(client, keys, stoppingToken);

            LogTrafficStarted(_options.Tenants.Count);
            var startedAt = time.GetTimestamp();
            await Task.WhenAll(_options.Tenants.Select(tenant => RunTenantAsync(client, tenant, keys[tenant.Id], startedAt, stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task WaitForGatewayAsync(HttpClient client, CancellationToken cancellationToken)
    {
        while (!await IsUpAsync(client, cancellationToken))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken);
        }
    }

#pragma warning disable CA1031 // Until the gateway answers, any failure just means "not yet".
    private static async Task<bool> IsUpAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync("/healthz", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
#pragma warning restore CA1031

    // Creates each tenant, or, if it already exists from an earlier run, gives it a fresh key. Safe to run again.
    private async Task<Dictionary<string, string>> SeedAsync(HttpClient client, CancellationToken cancellationToken)
    {
        await SendAdminAsync(
            client,
            HttpMethod.Put,
            $"/admin/prices/{_options.Model}",
            new { inputUsdPerMillionTokens = _options.InputUsdPerMillionTokens, outputUsdPerMillionTokens = _options.OutputUsdPerMillionTokens },
            cancellationToken);

        var keys = new Dictionary<string, string>();

        foreach (var tenant in _options.Tenants)
        {
            using var created = await SendAdminAsync(
                client,
                HttpMethod.Post,
                "/admin/tenants",
                new { id = tenant.Id, name = tenant.Name, budgetLimitUsd = tenant.BudgetUsd, budgetWindowSeconds = 3600 },
                cancellationToken);

            if (created.StatusCode == HttpStatusCode.Created)
            {
                keys[tenant.Id] = await ReadKeyAsync(created, cancellationToken);
                LogTenantReady(tenant.Id, "created");
                continue;
            }

            // It exists from an earlier run: bring its budget back to what this run expects, and give it a key we know.
            using var budget = await SendAdminAsync(
                client,
                HttpMethod.Put,
                $"/admin/tenants/{tenant.Id}/budget",
                new { budgetLimitUsd = tenant.BudgetUsd, budgetWindowSeconds = 3600 },
                cancellationToken);
            budget.EnsureSuccessStatusCode();

            using var rotated = await SendAdminAsync(client, HttpMethod.Post, $"/admin/tenants/{tenant.Id}/rotate-key", body: null, cancellationToken);
            rotated.EnsureSuccessStatusCode();
            keys[tenant.Id] = await ReadKeyAsync(rotated, cancellationToken);
            LogTenantReady(tenant.Id, "reset and given a new key");
        }

        return keys;
    }

    private async Task<HttpResponseMessage> SendAdminAsync(HttpClient client, HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", _options.AdminKey);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadKeyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return json.RootElement.GetProperty("apiKey").GetString()!;
    }

    // The gateway picks up a new tenant at its next catalog reload, so wait until it knows each key.
    private async Task WaitForKeysAsync(HttpClient client, Dictionary<string, string> keys, CancellationToken cancellationToken)
    {
        foreach (var (tenant, key) in keys)
        {
            while ((await SendCallAsync(client, key, runaway: false, cancellationToken)) == HttpStatusCode.Unauthorized)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), time, cancellationToken);
            }

            LogKeyAccepted(tenant);
        }
    }

    private async Task RunTenantAsync(HttpClient client, DemoTenant tenant, string key, long startedAt, CancellationToken cancellationToken)
    {
        var pending = 0d;
        var sent = 0;
        var refused = 0;
        var failed = 0;
        var lastReport = time.GetTimestamp();

        using var timer = new PeriodicTimer(Tick, time);

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var elapsed = time.GetElapsedTime(startedAt);
            var runaway = tenant.Runaway && elapsed >= _options.SpikeAfter && elapsed < _options.SpikeAfter + _options.SpikeFor;
            var perMinute = tenant.CallsPerMinute * (runaway ? _options.SpikeFactor : 1);

            // Calls arrive a little unevenly, as real traffic does.
            pending += perMinute / 60d * (0.6 + (Random.Shared.NextDouble() * 0.8));
            var due = (int)pending;
            pending -= due;

            var results = await Task.WhenAll(Enumerable.Range(0, due).Select(_ => SendCallAsync(client, key, runaway, cancellationToken)));
            sent += results.Count(r => r == HttpStatusCode.OK);
            refused += results.Count(r => r is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired);
            failed += results.Count(r => r != HttpStatusCode.OK && r is not (HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired));

            if (time.GetElapsedTime(lastReport) >= TimeSpan.FromMinutes(1))
            {
                LogMinute(tenant.Id, sent, refused, failed, runaway ? "runaway loop" : "normal");
                (sent, refused, failed, lastReport) = (0, 0, 0, time.GetTimestamp());
            }
        }
    }

#pragma warning disable CA1031 // A failed call is part of the picture the demo draws, not a reason to stop.
    private async Task<HttpStatusCode> SendCallAsync(HttpClient client, string key, bool runaway, CancellationToken cancellationToken)
    {
        // A runaway loop sends the same prompt over and over; normal callers vary theirs.
        var prompt = runaway
            ? "Summarise the following record again."
            : "Write a short note about topic number " + Random.Shared.Next(1, 500).ToString(CultureInfo.InvariantCulture) + new string('x', Random.Shared.Next(200, 900));

        var body = JsonSerializer.Serialize(new
        {
            model = _options.Model,
            max_tokens = 300,
            messages = new[] { new { role = "user", content = prompt } },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/openai/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return HttpStatusCode.ServiceUnavailable;
        }
    }
#pragma warning restore CA1031

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Tenant {TenantId} {Outcome}")]
    private partial void LogTenantReady(string tenantId, string outcome);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "The gateway accepts the key of tenant {TenantId}")]
    private partial void LogKeyAccepted(string tenantId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Sending traffic for {Tenants} tenants")]
    private partial void LogTrafficStarted(int tenants);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Last minute for {TenantId}: {Served} served, {Refused} refused, {Failed} failed ({Mode})")]
    private partial void LogMinute(string tenantId, int served, int refused, int failed, string mode);
}
