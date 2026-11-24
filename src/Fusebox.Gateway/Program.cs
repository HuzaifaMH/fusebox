using Fusebox.Application;
using Fusebox.Gateway;
using Fusebox.Gateway.Admin;
using Fusebox.Gateway.Middleware;
using Fusebox.Gateway.Observability;
using Fusebox.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFuseboxApplication();
builder.Services.AddFuseboxInfrastructure(builder.Configuration);
builder.Services.AddFuseboxGateway(builder.Configuration);
builder.Services.AddFuseboxObservability(builder.Configuration);
builder.Services.AddFuseboxAdmin(builder.Configuration);

var app = builder.Build();

// Created now rather than on the first request, so the degraded-mode gauge is reported from the start.
_ = app.Services.GetRequiredService<GatewayMetrics>();

app.MapHealthChecks("/healthz");
app.UseFuseboxAdmin();

app.MapReverseProxy(pipeline =>
{
    // Order matters: identify the tenant, start recording the request, enforce its budget, then let YARP forward the call.
    pipeline.UseMiddleware<TenantAuthenticationMiddleware>();
    pipeline.UseMiddleware<RequestTelemetryMiddleware>();
    pipeline.UseMiddleware<UpstreamBreakerMiddleware>();
    pipeline.UseMiddleware<BudgetEnforcementMiddleware>();
    pipeline.UseSessionAffinity();
    pipeline.UseLoadBalancing();
    pipeline.UsePassiveHealthChecks();
});

app.Run();
