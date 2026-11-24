namespace Fusebox.Application.Diagnosis;

public enum NarrationConfidence
{
    Low,
    Medium,
    High,
}

public enum NarrationSource
{
    /// <summary>Written by a fixed template from the evidence. Always available.</summary>
    Template,

    /// <summary>Written by an LLM from the evidence, and checked against it.</summary>
    Llm,
}

/// <summary>A plain-English explanation of an anomaly, with the evidence it rests on. Advisory: nothing acts on it.</summary>
/// <param name="Diagnosis">What changed and the most likely cause.</param>
/// <param name="Confidence">How much the evidence supports it.</param>
/// <param name="EvidenceIds">The evidence items the explanation relies on.</param>
/// <param name="Remediation">What to check or do.</param>
/// <param name="Source">Who wrote it.</param>
/// <param name="Model">The LLM that wrote it, if one did.</param>
/// <param name="FallbackReason">Why the template was used instead of an LLM, if it was asked for and did not deliver.</param>
public sealed record Narration(
    string Diagnosis,
    NarrationConfidence Confidence,
    IReadOnlyList<string> EvidenceIds,
    string Remediation,
    NarrationSource Source,
    string? Model = null,
    string? FallbackReason = null);

/// <summary>What is sent to an LLM.</summary>
public sealed record LlmPrompt(string System, string User);

/// <summary>What an LLM sent back.</summary>
public sealed record LlmReply(string Text, string Model);

/// <summary>Port: asks an LLM for a JSON answer. Implemented against any OpenAI-compatible endpoint.</summary>
public interface ILlmClient
{
    /// <summary>Sends the prompt and returns the model's reply. Throws if the call fails.</summary>
    ValueTask<LlmReply> CompleteJsonAsync(LlmPrompt prompt, CancellationToken cancellationToken);
}
