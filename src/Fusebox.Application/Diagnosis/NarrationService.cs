using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fusebox.Application.Diagnosis;

/// <summary>
/// Puts the evidence into words: with an LLM when one is configured and trustworthy, with the template otherwise.
/// An alert never waits on the LLM and never carries anything the evidence does not back up.
/// </summary>
public sealed partial class NarrationService
{
    private readonly DiagnosisOptions _options;
    private readonly ILlmClient? _llm;
    private readonly ILogger<NarrationService> _logger;

    public NarrationService(IOptions<DiagnosisOptions> options, ILogger<NarrationService> logger, ILlmClient? llm = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options.Value;
        _llm = llm;
        _logger = logger;
    }

    /// <param name="evidence">What was found.</param>
    /// <param name="allowLlm">False when a limit, such as the monthly cap, rules the LLM out.</param>
    /// <param name="cancellationToken">Cancelled if the work is abandoned.</param>
    public async ValueTask<Narration> NarrateAsync(Evidence evidence, bool allowLlm, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!_options.NarratorEnabled || _llm is null)
        {
            return TemplateNarrator.Narrate(evidence);
        }

        if (!allowLlm)
        {
            LogCapReached();
            return TemplateNarrator.Narrate(evidence, "the monthly limit on LLM explanations was reached");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.NarratorTimeout);

            var reply = await _llm.CompleteJsonAsync(NarrationPrompt.Build(evidence), timeout.Token);
            var check = NarrationValidator.Validate(reply.Text, reply.Model, evidence);

            if (check.IsValid)
            {
                return check.Narration!;
            }

            LogRefused(check.Problem!);
            return TemplateNarrator.Narrate(evidence, $"the model's answer was refused: {check.Problem}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogTimedOut(_options.NarratorTimeout);
            return TemplateNarrator.Narrate(evidence, $"the model did not answer within {_options.NarratorTimeout}");
        }
#pragma warning disable CA1031 // Whatever goes wrong reaching the model, the template is the answer.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(exception);
            return TemplateNarrator.Narrate(evidence, "the model could not be reached");
        }
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "The LLM's explanation was refused and the template was used instead: {Problem}")]
    private partial void LogRefused(string problem);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "The LLM did not answer within {Timeout}, so the template was used instead")]
    private partial void LogTimedOut(TimeSpan timeout);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "The LLM could not be reached, so the template was used instead")]
    private partial void LogFailed(Exception exception);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Information, Message = "The monthly limit on LLM explanations was reached, so the template was used instead")]
    private partial void LogCapReached();
}
