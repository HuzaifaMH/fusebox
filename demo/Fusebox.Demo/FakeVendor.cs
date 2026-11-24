using System.Text.Json;

namespace Fusebox.Demo;

/// <summary>A stand-in for a paid LLM API. It never sees a real prompt: it counts characters and makes up an answer.</summary>
internal static class FakeVendor
{
    public static async Task<IResult> ChatAsync(HttpRequest request, VendorOutage outage, CancellationToken cancellationToken)
    {
        if (outage.Active)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The demo vendor is having an outage");
        }

        using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        var root = body.RootElement;

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "demo-model" : "demo-model";
        var promptCharacters = 0;

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                promptCharacters += message.TryGetProperty("content", out var c) ? c.GetString()?.Length ?? 0 : 0;
            }
        }

        // Real vendors take a few hundred milliseconds. The variation gives the latency graph some shape.
        await Task.Delay(Random.Shared.Next(150, 700), cancellationToken);

        var promptTokens = Math.Max(1, promptCharacters / 4);
        var completionTokens = Random.Shared.Next(90, 220);

        return Results.Json(new
        {
            id = $"chatcmpl-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = "This is a made-up answer from the demo vendor." },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
            },
        });
    }
}
