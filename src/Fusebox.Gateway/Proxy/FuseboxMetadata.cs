namespace Fusebox.Gateway.Proxy;

/// <summary>
/// The route and cluster metadata keys Fusebox reads from the YARP configuration.
/// Keys use '.' rather than ':' because ':' is the configuration path separator, so a key containing it
/// would be split into nested sections and never reach the route.
/// </summary>
internal static class FuseboxMetadata
{
    /// <summary>Route: the name of the <c>ILlmProtocol</c> that prices and meters this route's calls.</summary>
    public const string Protocol = "Fusebox.Protocol";

    /// <summary>Route: set to "none" to explicitly pass calls through without metering, for example free endpoints.</summary>
    public const string Metering = "Fusebox.Metering";

    public const string MeteringNone = "none";

    /// <summary>
    /// Route: what to do when the budget store is unreachable, "FailOpenDegraded" (the default) or "FailClosed".
    /// Set "FailClosed" on routes where any unverified spend is too much, such as payments.
    /// </summary>
    public const string FailureMode = "Fusebox.FailureMode";

    /// <summary>Cluster: the name of the vendor API key to send upstream.</summary>
    public const string UpstreamKey = "Fusebox.UpstreamKey";
}
