using System.Net.Http.Headers;
using Fusebox.Application.Budgeting;
using Fusebox.Application.Telemetry;
using Fusebox.Application.Upstream;
using Fusebox.Gateway.Configuration;
using Fusebox.Gateway.Proxy;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Fusebox.Gateway;

internal static class ServiceCollectionExtensions
{
    /// <summary>Registers the gateway's own options, health checks and the YARP proxy with Fusebox's request transforms.</summary>
    public static IServiceCollection AddFuseboxGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<BudgetGuardOptions>()
            .Bind(configuration.GetSection("Fusebox:Budgeting"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RepeatDetectionOptions>()
            .Bind(configuration.GetSection(RepeatDetectionOptions.SectionName))
            .Validate(o => o.Window >= TimeSpan.FromSeconds(1), "Window must be at least one second.")
            .Validate(o => o.MaxTracked >= 1, "MaxTracked must be at least 1.")
            .ValidateOnStart();
        services.AddSingleton<RepeatDetector>();

        services.AddOptions<UpstreamBreakerOptions>()
            .Bind(configuration.GetSection(UpstreamBreakerOptions.SectionName))
            .Validate(o => o.FailureThreshold >= 1, "FailureThreshold must be at least 1.")
            .Validate(o => o.OpenFor >= TimeSpan.FromSeconds(1), "OpenFor must be at least one second.")
            .ValidateOnStart();
        services.AddSingleton<UpstreamBreakers>();

        services.AddOptions<UpstreamCredentialsOptions>()
            .Bind(configuration.GetSection(UpstreamCredentialsOptions.SectionName));

        services.AddProblemDetails();
        services.AddHealthChecks();

        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetSection("ReverseProxy"))
            .AddTransforms(AddUpstreamTransforms);

        return services;
    }

    private static void AddUpstreamTransforms(TransformBuilderContext context)
    {
        // The client's Fusebox key must never reach the vendor.
        context.AddRequestHeaderRemove("Authorization");

        // Ask for an uncompressed body so the token usage in it can be read.
        context.AddRequestHeaderRemove("Accept-Encoding");

        if (context.Cluster?.Metadata is not { } metadata
            || !metadata.TryGetValue(FuseboxMetadata.UpstreamKey, out var keyName))
        {
            return;
        }

        var credentials = context.Services.GetRequiredService<IOptionsMonitor<UpstreamCredentialsOptions>>();

        // Read per request, so a rotated vendor key takes effect without a restart.
        context.AddRequestTransform(transform =>
        {
            transform.ProxyRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", credentials.CurrentValue.Require(keyName));
            return ValueTask.CompletedTask;
        });
    }
}
