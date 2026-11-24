using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Fusebox.Gateway.Observability;

internal static class ObservabilityExtensions
{
    /// <summary>
    /// Collects Fusebox's own meters, YARP's and the runtime's, and pushes them with OpenTelemetry when an address is set.
    /// Per-tenant and per-model views are read from the database rollups instead, to keep label counts low.
    /// </summary>
    public static IServiceCollection AddFuseboxObservability(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SectionName))
            .Validate(o => o.ExportInterval >= TimeSpan.FromSeconds(1), "ExportInterval must be at least one second.")
            .Validate(
                o => o.OtlpEndpoint.Length == 0 || Uri.TryCreate(o.OtlpEndpoint, UriKind.Absolute, out _),
                "OtlpEndpoint must be an absolute address.")
            .ValidateOnStart();

        services.AddSingleton<GatewayMetrics>();

        var options = configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("fusebox"))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(GatewayMetrics.MeterName, "Fusebox.Telemetry", "Fusebox.Alerts", "Yarp.ReverseProxy")
                    .AddRuntimeInstrumentation();

                if (options.OtlpEndpoint.Length > 0)
                {
                    metrics.AddOtlpExporter((exporter, reader) =>
                    {
                        exporter.Endpoint = new Uri(options.OtlpEndpoint);
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = (int)options.ExportInterval.TotalMilliseconds;
                    });
                }
            });

        return services;
    }
}
