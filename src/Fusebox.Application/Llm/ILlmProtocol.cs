using System.Diagnostics.CodeAnalysis;
using Fusebox.Domain;

namespace Fusebox.Application.Llm;

/// <summary>
/// Port: understands one vendor's wire format. Adding a vendor means adding one implementation,
/// with no change to the budgeting logic.
/// </summary>
public interface ILlmProtocol
{
    /// <summary>The name routes use to select this protocol, for example "openai-chat".</summary>
    string Name { get; }

    /// <summary>Reads the cost-relevant facts from a request body. Returns false if the body is not a valid request.</summary>
    bool TryParseRequest(ReadOnlyMemory<byte> body, [NotNullWhen(true)] out LlmRequestInfo? request);

    /// <summary>Reads the token usage the vendor reported in a complete, non-streamed response body.</summary>
    bool TryExtractUsage(ReadOnlyMemory<byte> responseBody, out TokenUsage usage);

    /// <summary>
    /// Returns the request body changed so that the vendor reports token usage at the end of a streamed response.
    /// Without this, a streamed call cannot be priced from real usage.
    /// </summary>
    ReadOnlyMemory<byte> WithStreamUsage(ReadOnlyMemory<byte> requestBody);

    /// <summary>
    /// Reads usage from the data of one streamed event. Returns false for the many events that carry none.
    /// </summary>
    bool TryReadStreamUsage(ReadOnlyMemory<byte> eventData, out StreamUsage usage);
}
