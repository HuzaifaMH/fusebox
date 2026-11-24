namespace Fusebox.Domain;

/// <summary>Tokens an LLM call consumed, as reported by the vendor.</summary>
public readonly record struct TokenUsage
{
    public TokenUsage(long inputTokens, long outputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public long InputTokens { get; }

    public long OutputTokens { get; }
}
