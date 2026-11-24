using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fusebox.Application.Diagnosis;
using Microsoft.Extensions.Options;

namespace Fusebox.Infrastructure.Diagnosis;

/// <summary>Where the LLM that explains anomalies is reached.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Fusebox:Diagnosis:Llm";

    /// <summary>
    /// The base address of any OpenAI-compatible API. It can be the vendor directly, or Fusebox itself, so that the
    /// explanations are budgeted, limited and recorded like any other tenant's calls.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The path of the chat completions endpoint under the base address.</summary>
    public string Path { get; set; } = "v1/chat/completions";

    public string Model { get; set; } = string.Empty;

    /// <summary>The key sent as a bearer token. Supply it through an environment variable, never a committed file.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The most tokens the model may write. Explanations are short, and this caps what one costs.</summary>
    public int MaxOutputTokens { get; set; } = 500;
}

/// <summary>
/// Asks any OpenAI-compatible chat completions API for a JSON answer. Sends the prompt it is given and nothing else,
/// and keeps nothing: the answer is checked and used or thrown away by the caller.
/// </summary>
internal sealed class OpenAiCompatibleLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly IOptions<LlmOptions> _options;

    public OpenAiCompatibleLlmClient(IOptions<LlmOptions> options)
        : this(options, new HttpClient(), ownsClient: true)
    {
    }

    /// <summary>For tests, to answer from a fake instead of the network.</summary>
    internal OpenAiCompatibleLlmClient(IOptions<LlmOptions> options, HttpClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);

        _options = options;
        _client = client;
        _ownsClient = ownsClient;
    }

    public async ValueTask<LlmReply> CompleteJsonAsync(LlmPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var settings = _options.Value;
        var address = new Uri(new Uri(settings.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute), settings.Path.TrimStart('/'));

        using var request = new HttpRequestMessage(HttpMethod.Post, address)
        {
            Content = new StringContent(BuildBody(settings, prompt), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var response = await _client.SendAsync(request, cancellationToken);

        // The body is not included in the error: it may echo the prompt.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The LLM answered with status {(int)response.StatusCode}.", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return Parse(document.RootElement, settings);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static string BuildBody(LlmOptions settings, LlmPrompt prompt) =>
        JsonSerializer.Serialize(new
        {
            model = settings.Model,
            messages = new[]
            {
                new { role = "system", content = prompt.System },
                new { role = "user", content = prompt.User },
            },
            temperature = 0,
            max_tokens = settings.MaxOutputTokens,
            response_format = new { type = "json_object" },
        });

    private static LlmReply Parse(JsonElement root, LlmOptions settings)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            var model = root.TryGetProperty("model", out var named) && named.ValueKind == JsonValueKind.String
                ? named.GetString()!
                : settings.Model;

            return new LlmReply(content.GetString()!, model);
        }

        throw new InvalidOperationException("The LLM's answer did not have the expected shape.");
    }
}
