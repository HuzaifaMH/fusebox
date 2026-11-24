using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Fusebox.Gateway.Tests;

/// <summary>Builds and sends chat-completion calls, shared by every gateway test class.</summary>
internal static class ChatRequests
{
    /// <summary>A tiny prompt (about one input token) with the given output cap.</summary>
    public static string Body(string model = GatewayFactory.Model, int maxTokens = 100, bool stream = false) =>
        JsonSerializer.Serialize(new
        {
            model,
            max_tokens = maxTokens,
            stream,
            messages = new[] { new { role = "user", content = "hi" } },
        });

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string body,
        string? apiKey = GatewayFactory.TenantKey,
        string path = "/openai/v1/chat/completions")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return client.SendAsync(request);
    }

    public static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
