using Microsoft.Extensions.Options;

namespace Fusebox.Demo;

/// <summary>Makes the stand-in vendor fail for a while, so the demo can show Fusebox cutting a failing vendor off.</summary>
internal sealed class VendorOutage(IOptions<DemoOptions> options, TimeProvider time)
{
    private readonly long _startedAt = time.GetTimestamp();

    public bool Active
    {
        get
        {
            var elapsed = time.GetElapsedTime(_startedAt);
            return elapsed >= options.Value.OutageAfter && elapsed < options.Value.OutageAfter + options.Value.OutageFor;
        }
    }
}
