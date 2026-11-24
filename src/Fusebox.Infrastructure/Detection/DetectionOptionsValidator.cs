using Fusebox.Application.Detection;
using Fusebox.Infrastructure.Telemetry;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Detection;

/// <summary>Rejects settings that would make detection meaningless or unsafe, and detection without the data it reads.</summary>
internal sealed class DetectionOptionsValidator(IOptions<TelemetryOptions> telemetry) : IValidateOptions<DetectionOptions>
{
    public ValidateOptionsResult Validate(string? name, DetectionOptions options)
    {
        var failures = new List<string>();

        if (options.Enabled && telemetry.Value.Store != TelemetryStoreKind.Postgres)
        {
            failures.Add("Detection reads the per-minute totals in PostgreSQL, so it needs Fusebox:Telemetry:Store to be Postgres.");
        }

        if (options.Enabled && options.Lag <= telemetry.Value.RollupInterval)
        {
            failures.Add("Lag must be longer than the telemetry RollupInterval, or the newest minute may be judged before its totals are complete.");
        }

        if (options.EvaluationInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("EvaluationInterval must be at least one second.");
        }

        if (options.WindowMinutes is < 1 or > 60)
        {
            failures.Add("WindowMinutes must be between 1 and 60.");
        }

        if (options.MinMinutesWithData < 1 || options.MinMinutesWithData > options.WindowMinutes)
        {
            failures.Add("MinMinutesWithData must be between 1 and WindowMinutes.");
        }

        if (options.ClearRatio <= 1 || options.FireRatio <= options.ClearRatio)
        {
            failures.Add("The ratios must satisfy 1 < ClearRatio < FireRatio, or an anomaly could end the moment it starts.");
        }

        if (options.MinRobustZ < 0 || options.CalmEvaluationsToClear < 1 || options.MinRequestsInWindow < 0 || options.MinForwardedPerMinute < 0)
        {
            failures.Add("MinRobustZ, MinRequestsInWindow and MinForwardedPerMinute cannot be negative, and CalmEvaluationsToClear must be at least 1.");
        }

        if (options.Cooldown < TimeSpan.Zero || options.BaselineRefreshInterval < TimeSpan.FromSeconds(1))
        {
            failures.Add("Cooldown cannot be negative and BaselineRefreshInterval must be at least one second.");
        }

        if (options.SeasonalWeeks is < 0 or > 26 || options.MinSeasonalSamples < 1 || options.MinRecentSamples < 1)
        {
            failures.Add("SeasonalWeeks must be between 0 and 26, and the minimum sample counts at least 1.");
        }

        if (options.RecentBaseline < TimeSpan.FromMinutes(options.MinRecentSamples))
        {
            failures.Add("RecentBaseline must be at least MinRecentSamples minutes long, or a recent baseline could never be learned.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
