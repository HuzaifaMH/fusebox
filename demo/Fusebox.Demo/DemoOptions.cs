namespace Fusebox.Demo;

/// <summary>Settings for the demo. Section <c>Demo</c>.</summary>
public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>Where the gateway listens, as seen from this container.</summary>
    public string GatewayUrl { get; set; } = "http://gateway:8080";

    /// <summary>The admin key the gateway was started with. Demo value only.</summary>
    public string AdminKey { get; set; } = "demo-admin-key";

    /// <summary>When the runaway tenant starts looping, counted from the start of traffic.</summary>
    public TimeSpan SpikeAfter { get; set; } = TimeSpan.FromMinutes(12);

    /// <summary>How long the runaway lasts.</summary>
    public TimeSpan SpikeFor { get; set; } = TimeSpan.FromMinutes(8);

    /// <summary>When the stand-in vendor starts failing every call with a 503, counted from when the demo started.</summary>
    public TimeSpan OutageAfter { get; set; } = TimeSpan.FromMinutes(24);

    /// <summary>How long the vendor outage lasts.</summary>
    public TimeSpan OutageFor { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How many times its normal rate the runaway tenant calls at.</summary>
    public int SpikeFactor { get; set; } = 10;

    /// <summary>The model every call asks for, and its price per million tokens in dollars.</summary>
    public string Model { get; set; } = "demo-model";

    public decimal InputUsdPerMillionTokens { get; set; } = 30m;

    public decimal OutputUsdPerMillionTokens { get; set; } = 60m;

    public IList<DemoTenant> Tenants { get; } =
    [
        // The runaway: normally calm, then loops.
        new DemoTenant("acme", "Acme Corp", BudgetUsd: 30m, CallsPerMinute: 8, Runaway: true),

        // A tenant whose budget is too small for its traffic, so it runs into the limit and is refused.
        new DemoTenant("globex", "Globex", BudgetUsd: 0.75m, CallsPerMinute: 5, Runaway: false),
    ];
}

public sealed record DemoTenant(string Id, string Name, decimal BudgetUsd, int CallsPerMinute, bool Runaway);
