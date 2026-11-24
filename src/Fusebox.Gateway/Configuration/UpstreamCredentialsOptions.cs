namespace Fusebox.Gateway.Configuration;

/// <summary>
/// The vendor API keys Fusebox uses when forwarding calls. Client apps never hold these:
/// they send a Fusebox key, and Fusebox swaps in the vendor key. Supply them through environment variables
/// or a secrets store, never a committed file.
/// </summary>
public sealed class UpstreamCredentialsOptions
{
    public const string SectionName = "Fusebox:Upstreams";

    /// <summary>Vendor API keys by the name a cluster refers to in its "Fusebox.UpstreamKey" metadata.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    /// <exception cref="InvalidOperationException">No key is configured under that name.</exception>
    public string Require(string name) =>
        Keys.TryGetValue(name, out var key) && !string.IsNullOrWhiteSpace(key)
            ? key
            : throw new InvalidOperationException($"No upstream API key is configured under '{SectionName}:Keys:{name}'.");
}
