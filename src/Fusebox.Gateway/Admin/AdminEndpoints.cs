using Fusebox.Application.Admin;
using Fusebox.Application.Alerting;
using Fusebox.Application.Reconciliation;
using Fusebox.Domain;
using Fusebox.Gateway.Http;

namespace Fusebox.Gateway.Admin;

internal static class AdminEndpoints
{
    private const string Prefix = "/admin";

    /// <summary>Maps the admin API under <c>/admin</c>. Every route needs the admin key.</summary>
    public static IEndpointRouteBuilder MapFuseboxAdmin(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var admin = app.MapGroup(Prefix).AddEndpointFilter<AdminKeyFilter>();
        var tenants = admin.MapGroup("/tenants").WithTags("Tenants");

        tenants.MapGet("/", async (CatalogAdminService service, CancellationToken ct) =>
                Results.Ok((await service.ListTenantsAsync(ct)).Select(TenantResponse.From)))
            .WithSummary("List tenants, including disabled ones.");

        tenants.MapGet("/{id}", (string id, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () => Results.Ok(TenantResponse.From(await service.GetTenantAsync(id, ct)))))
            .WithSummary("Get one tenant.");

        tenants.MapPost("/", (CreateTenantRequest request, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    var created = await service.CreateTenantAsync(
                        request.Id,
                        request.Name,
                        Money.FromDollars(request.BudgetLimitUsd),
                        TimeSpan.FromSeconds(request.BudgetWindowSeconds),
                        ct);

                    return Results.Created($"{Prefix}/tenants/{created.Tenant.Id.Value}", new CreatedTenantResponse(TenantResponse.From(created.Tenant), created.ApiKey));
                }))
            .WithSummary("Create a tenant. The response holds its API key, which is shown only once.");

        tenants.MapPut("/{id}/budget", (string id, SetBudgetRequest request, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    await service.SetBudgetAsync(id, Money.FromDollars(request.BudgetLimitUsd), TimeSpan.FromSeconds(request.BudgetWindowSeconds), ct);
                    return Results.NoContent();
                }))
            .WithSummary("Change a tenant's budget. Takes effect when the gateway next reloads, within seconds.");

        tenants.MapPost("/{id}/enable", (string id, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    await service.SetEnabledAsync(id, enabled: true, ct);
                    return Results.NoContent();
                }))
            .WithSummary("Let a disabled tenant call again.");

        tenants.MapPost("/{id}/disable", (string id, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    await service.SetEnabledAsync(id, enabled: false, ct);
                    return Results.NoContent();
                }))
            .WithSummary("Stop a tenant's calls. They are refused as unauthenticated.");

        tenants.MapPost("/{id}/rotate-key", (string id, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    var rotated = await service.RotateKeyAsync(id, ct);
                    return Results.Ok(new RotatedKeyResponse(rotated.TenantId.Value, rotated.ApiKey));
                }))
            .WithSummary("Replace a tenant's key. The old key stops working and the new one is shown only once.");

        tenants.MapPut("/{id}/pricing", (string id, SetPricingRequest request, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    await service.SetPriceMultiplierAsync(id, request.PriceMultiplier, ct);
                    return Results.NoContent();
                }))
            .WithSummary("Set what a tenant pays compared with list price, for example 0.85 for a 15% discount.");

        tenants.MapGet("/{id}/prices", (string id, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () => Results.Ok((await service.ListTenantPricesAsync(id, ct)).Select(PriceResponse.From))))
            .WithSummary("List the rates negotiated for one tenant, including ones scheduled for later.");

        tenants.MapPut("/{id}/prices/{*model}", (string id, string model, SetPriceRequest request, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    await service.SetTenantPriceAsync(
                        id,
                        model,
                        Money.FromDollars(request.InputUsdPerMillionTokens),
                        Money.FromDollars(request.OutputUsdPerMillionTokens),
                        request.EffectiveFrom,
                        ct);
                    return Results.NoContent();
                }))
            .WithSummary("Give a tenant its own rate for a model. It replaces the list price, and the tenant's multiplier, for that model.");

        tenants.MapDelete("/{id}/prices/{*model}", (string id, string model, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    await service.RemoveTenantPriceAsync(id, model, ct);
                    return Results.NoContent();
                }))
            .WithSummary("Remove a tenant's own rate for a model, so it pays the list price again.");

        var prices = admin.MapGroup("/prices").WithTags("Prices");

        prices.MapGet("/", async (CatalogAdminService service, CancellationToken ct) =>
                Results.Ok((await service.ListPricesAsync(ct)).Select(PriceResponse.From)))
            .WithSummary("List every price, newest first for each model, including ones scheduled for later.");

        prices.MapPut("/{*model}", (string model, SetPriceRequest request, CatalogAdminService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    await service.SetPriceAsync(
                        model,
                        Money.FromDollars(request.InputUsdPerMillionTokens),
                        Money.FromDollars(request.OutputUsdPerMillionTokens),
                        request.EffectiveFrom,
                        ct);
                    return Results.NoContent();
                }))
            .WithSummary("Set a model's price from now, or from a future time to schedule a change.");

        admin.MapPost("/reconciliation", (ReconcileRequest request, ReconciliationService service, CancellationToken ct) =>
                Guarded(async () =>
                {
                    var lines = request.Lines.Select(l => new VendorLine(l.Model, Money.FromDollars(l.VendorCostUsd))).ToList();
                    var report = await service.ReconcileAsync(new ReconciliationRequest(request.PeriodStart, request.PeriodEnd, lines), ct);
                    return Results.Ok(ReconciliationResponse.From(report));
                }))
            .WithTags("Reconciliation")
            .WithSummary("Compare what Fusebox charged in a period with what the vendor billed. Changes nothing.")
            .WithDescription("Give the period (whole hours, UTC, ended at least three hours ago) and either one total or a line per model from the vendor's statement. The result shows where the two differ and by how much, and what factor on Fusebox's prices would close the gap.");

        var insight = admin.MapGroup("/").WithTags("Insight");

        insight.MapGet("/anomalies", async (IAlertFeed feed, TimeProvider time, int? hours, CancellationToken ct) =>
            {
                var since = time.GetUtcNow().AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 14));
                return Results.Ok((await feed.LoadAnomaliesAsync(since, ct)).Select(AnomalyResponse.From));
            })
            .WithSummary("Anomalies that are open, or that ended in the last day (or the given number of hours), with their diagnosis.");

        insight.MapGet("/alerts", async (IAlertStore store, CancellationToken ct) =>
            {
                var open = new List<AlertRecord>();

                foreach (var kind in Enum.GetValues<AlertKind>())
                {
                    open.AddRange(await store.FindOpenByKindAsync(kind, ct));
                }

                return Results.Ok(open.OrderByDescending(a => a.Severity).ThenBy(a => a.OpenedAt).Select(AlertResponse.From));
            })
            .WithSummary("Alerts that are open now.");

        return app;
    }

    // Turns the service's exceptions into the same problem documents the rest of Fusebox returns.
    private static async Task<IResult> Guarded(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (AdminNotFoundException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: exception.Message, type: "urn:fusebox:problem:not-found");
        }
        catch (AdminConflictException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already exists", detail: exception.Message, type: "urn:fusebox:problem:conflict");
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return ProblemResults.InvalidRequest(exception is ArgumentException ? exception.Message : "A number in the request is too large.");
        }
    }
}
