using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Fusebox.Gateway.Tests;

internal sealed record ReceivedRequest(string Path, string? Authorization, string? AcceptEncoding, string Body);

/// <summary>
/// A stand-in for a vendor API such as OpenAI, listening on a real loopback port so the gateway's
/// proxy talks to it over real HTTP. It records every call and answers with whatever a test configured.
/// </summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    public const string CompletionWithUsage =
        """{"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"}}],"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":30}}""";

    private const string ContentEvent = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}],\"usage\":null}\n\n";
    private const string DoneEvent = "data: [DONE]\n\n";

    /// <summary>A stream that ends with the usage event, as the vendor sends when asked to report usage.</summary>
    public const string StreamWithUsage =
        ContentEvent
        + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":20,\"total_tokens\":30}}\n\n"
        + DoneEvent;

    public const string StreamWithoutUsage = ContentEvent + DoneEvent;

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ReceivedRequest> _requests = new();

    private FakeUpstream(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }

    public IReadOnlyCollection<ReceivedRequest> Requests => _requests;

    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    public string ResponseBody { get; set; } = CompletionWithUsage;

    /// <summary>Set to "text/event-stream" to answer as a stream, one event at a time.</summary>
    public string ContentType { get; set; } = "application/json";

    /// <summary>When set, responses wait for this task, so a test can hold calls in flight.</summary>
    public Task? Gate { get; set; }

    public static async Task<FakeUpstream> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        FakeUpstream? upstream = null;

        app.MapPost("/v1/chat/completions", (HttpContext context) => upstream!.HandleAsync(context));
        app.MapGet("/v1/models", (HttpContext context) => upstream!.HandleAsync(context));

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        upstream = new FakeUpstream(app, new Uri(address));
        return upstream;
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();

        _requests.Enqueue(new ReceivedRequest(
            context.Request.Path,
            context.Request.Headers.Authorization.ToString(),
            context.Request.Headers.AcceptEncoding.ToString(),
            body));

        if (Gate is not null)
        {
            await Gate;
        }

        context.Response.StatusCode = StatusCode;
        context.Response.ContentType = ContentType;

        if (ContentType != "text/event-stream")
        {
            await context.Response.WriteAsync(ResponseBody);
            return;
        }

        // Send each event on its own and flush, as a real streaming vendor does.
        foreach (var sseEvent in ResponseBody.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            await context.Response.WriteAsync(sseEvent + "\n\n");
            await context.Response.Body.FlushAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
