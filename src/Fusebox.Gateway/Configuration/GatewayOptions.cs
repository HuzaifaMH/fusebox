using System.ComponentModel.DataAnnotations;

namespace Fusebox.Gateway.Configuration;

public sealed class GatewayOptions
{
    public const string SectionName = "Fusebox:Gateway";

    /// <summary>The largest request body that will be read to price a call. Larger bodies are rejected with 413.</summary>
    [Range(1_024, 64 * 1_024 * 1_024)]
    public int MaxRequestBodyBytes { get; set; } = 1_048_576;

    /// <summary>The most response bytes kept in memory to read token usage. A bigger response is charged at its reservation.</summary>
    [Range(1_024, 64 * 1_024 * 1_024)]
    public int MaxCapturedResponseBytes { get; set; } = 4_194_304;

    /// <summary>The largest single streamed event that is held back to read usage. A bigger event is forwarded as is.</summary>
    [Range(1_024, 16 * 1_024 * 1_024)]
    public int MaxStreamEventBytes { get; set; } = 1_048_576;

    /// <summary>
    /// How long settlement may take after a call finishes. Settlement ignores client disconnects,
    /// because the vendor bills a call whether or not the client is still listening.
    /// </summary>
    public TimeSpan SettlementTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
