using Fusebox.Domain;

namespace Fusebox.Application.Llm;

/// <summary>Token usage read from one event of a streamed response.</summary>
/// <param name="Tokens">The usage the vendor reported.</param>
/// <param name="IsUsageOnly">
/// True if the event exists only to carry usage and holds no content for the caller. Fusebox can hide such an
/// event from callers who did not ask for it.
/// </param>
public readonly record struct StreamUsage(TokenUsage Tokens, bool IsUsageOnly);
