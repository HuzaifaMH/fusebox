using Fusebox.Application.Detection;
using Fusebox.Application.Diagnosis;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Diagnosis;

/// <summary>Rejects settings that would make diagnosis meaningless, and diagnosis without the data and the LLM it needs.</summary>
internal sealed class DiagnosisOptionsValidator(IOptions<DetectionOptions> detection, IOptions<LlmOptions> llm) : IValidateOptions<DiagnosisOptions>
{
    public ValidateOptionsResult Validate(string? name, DiagnosisOptions options)
    {
        var failures = new List<string>();

        if (options.Enabled && !detection.Value.Enabled)
        {
            failures.Add("Diagnosis explains anomalies the detector finds, so it needs Fusebox:Detection:Enabled to be true.");
        }

        if (options.Interval < TimeSpan.FromSeconds(1))
        {
            failures.Add("Interval must be at least one second.");
        }

        if (options.BeforeMinutes < 5 || options.MinWindowMinutes < 1 || options.MinBeforeMinutes < 1 || options.MinRequests < 0)
        {
            failures.Add("BeforeMinutes must be at least 5 and the minimum data settings must be positive.");
        }

        if (options.BatchSize < 1 || options.MaxSegments is < 1 or > 10)
        {
            failures.Add("BatchSize must be at least 1 and MaxSegments between 1 and 10.");
        }

        if (options.MaxAnomalyAge < TimeSpan.FromMinutes(1))
        {
            failures.Add("MaxAnomalyAge must be at least one minute.");
        }

        if (options.NarratorEnabled)
        {
            if (options.MaxNarrationsPerMonth < 0 || options.NarratorTimeout < TimeSpan.FromSeconds(1) || options.NarratorTimeout > TimeSpan.FromMinutes(2))
            {
                failures.Add("MaxNarrationsPerMonth cannot be negative and NarratorTimeout must be between one second and two minutes.");
            }

            var settings = llm.Value;
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out _) || string.IsNullOrWhiteSpace(settings.Model) || string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                failures.Add("The LLM narrator needs Fusebox:Diagnosis:Llm BaseUrl (an absolute address), Model and ApiKey.");
            }

            if (settings.MaxOutputTokens is < 50 or > 4_000)
            {
                failures.Add("Llm.MaxOutputTokens must be between 50 and 4000.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
