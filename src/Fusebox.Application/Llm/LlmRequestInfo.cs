namespace Fusebox.Application.Llm;

/// <summary>The cost-relevant facts about an LLM request, extracted from its body.</summary>
/// <param name="Model">The requested model.</param>
/// <param name="MaxOutputTokens">The caller's output cap per choice, or null if none was set.</param>
/// <param name="Choices">How many completions were requested. Each can produce up to the output cap.</param>
/// <param name="PromptCharacters">Total characters of prompt text, used to estimate input tokens.</param>
/// <param name="IsStreaming">Whether the response is streamed.</param>
/// <param name="ClientRequestedStreamUsage">Whether the caller itself asked the vendor to report usage in the stream.</param>
public sealed record LlmRequestInfo(
    string Model,
    long? MaxOutputTokens,
    int Choices,
    long PromptCharacters,
    bool IsStreaming,
    bool ClientRequestedStreamUsage = false);
