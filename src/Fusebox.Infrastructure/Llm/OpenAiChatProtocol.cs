using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fusebox.Application.Llm;
using Fusebox.Domain;

namespace Fusebox.Infrastructure.Llm;

/// <summary>The OpenAI "chat completions" wire format, which many other vendors also accept.</summary>
internal sealed class OpenAiChatProtocol : ILlmProtocol
{
    public const string ProtocolName = "openai-chat";

    // Bounds that reject absurd values before they can overflow cost arithmetic.
    private const long MaxSaneTokens = 10_000_000;
    private const long MaxSaneChoices = 1_000;

    public string Name => ProtocolName;

    public bool TryParseRequest(ReadOnlyMemory<byte> body, [NotNullWhen(true)] out LlmRequestInfo? request)
    {
        request = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("model", out var modelElement)
                || modelElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(modelElement.GetString())
                || !root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            if (!TryReadOptionalCount(root, "max_completion_tokens", MaxSaneTokens, out var maxCompletion)
                || !TryReadOptionalCount(root, "max_tokens", MaxSaneTokens, out var maxTokens)
                || !TryReadOptionalCount(root, "n", MaxSaneChoices, out var choices))
            {
                return false;
            }

            var promptCharacters = CountMessageCharacters(messages)
                + RawLength(root, "tools")
                + RawLength(root, "functions");

            var isStreaming = root.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True;

            request = new LlmRequestInfo(
                modelElement.GetString()!,
                maxCompletion ?? maxTokens,
                (int)(choices ?? 1),
                promptCharacters,
                isStreaming,
                ClientRequestedStreamUsage: IncludesStreamUsage(root));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool TryExtractUsage(ReadOnlyMemory<byte> responseBody, out TokenUsage usage)
    {
        usage = default;

        try
        {
            using var document = JsonDocument.Parse(responseBody);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("usage", out var usageElement)
                && usageElement.ValueKind == JsonValueKind.Object
                && TryReadNonNegative(usageElement, "prompt_tokens", out var input)
                && TryReadNonNegative(usageElement, "completion_tokens", out var output))
            {
                usage = new TokenUsage(input, output);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public ReadOnlyMemory<byte> WithStreamUsage(ReadOnlyMemory<byte> requestBody)
    {
        try
        {
            if (JsonNode.Parse(requestBody.Span) is not JsonObject root)
            {
                return requestBody;
            }

            // Keep any other stream options the caller set.
            var options = root["stream_options"] as JsonObject ?? new JsonObject();
            options["include_usage"] = true;
            root["stream_options"] = options;

            return JsonSerializer.SerializeToUtf8Bytes(root);
        }
        catch (JsonException)
        {
            return requestBody;
        }
    }

    public bool TryReadStreamUsage(ReadOnlyMemory<byte> eventData, out StreamUsage usage)
    {
        usage = default;

        try
        {
            using var document = JsonDocument.Parse(eventData);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("usage", out var usageElement)
                || usageElement.ValueKind != JsonValueKind.Object
                || !TryReadNonNegative(usageElement, "prompt_tokens", out var input)
                || !TryReadNonNegative(usageElement, "completion_tokens", out var output))
            {
                return false;
            }

            // With usage reporting on, the vendor ends the stream with an extra event that has no choices.
            var isUsageOnly = !root.TryGetProperty("choices", out var choices)
                || (choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 0);

            usage = new StreamUsage(new TokenUsage(input, output), isUsageOnly);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IncludesStreamUsage(JsonElement root) =>
        root.TryGetProperty("stream_options", out var options)
        && options.ValueKind == JsonValueKind.Object
        && options.TryGetProperty("include_usage", out var include)
        && include.ValueKind == JsonValueKind.True;

    private static long CountMessageCharacters(JsonElement messages)
    {
        long total = 0;

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content))
            {
                continue;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                total += content.GetString()!.Length;
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        total += text.GetString()!.Length;
                    }
                }
            }
        }

        return total;
    }

    private static long RawLength(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) ? element.GetRawText().Length : 0;

    /// <summary>Reads an optional positive integer. Returns false only when the value is present but invalid.</summary>
    private static bool TryReadOptionalCount(JsonElement root, string property, long max, out long? value)
    {
        value = null;

        if (!root.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var number) || number < 1 || number > max)
        {
            return false;
        }

        value = number;
        return true;
    }

    private static bool TryReadNonNegative(JsonElement parent, string property, out long value)
    {
        value = 0;

        return parent.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value >= 0;
    }
}
