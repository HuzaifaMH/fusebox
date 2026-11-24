namespace Fusebox.Gateway.Observability;

/// <summary>Where the gateway sends its metrics. Section <c>Fusebox:Observability</c>.</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Fusebox:Observability";

    /// <summary>
    /// The full address metrics are pushed to over OTLP/HTTP, for example Prometheus at
    /// <c>http://prometheus:9090/api/v1/otlp/v1/metrics</c>. Empty means metrics are collected but not exported.
    /// </summary>
    public string OtlpEndpoint { get; set; } = string.Empty;

    /// <summary>How often metrics are pushed.</summary>
    public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(15);
}
