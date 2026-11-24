using Fusebox.Demo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DemoOptions>().Bind(builder.Configuration.GetSection(DemoOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VendorOutage>();
builder.Services.AddSingleton<AlertSink>();
builder.Services.AddSingleton<TrafficGenerator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<TrafficGenerator>());
builder.Services.AddHttpClient(TrafficGenerator.ClientName);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok());

// The stand-in vendor: an OpenAI-style chat endpoint that answers after a short, varying delay.
app.MapPost("/v1/chat/completions", FakeVendor.ChatAsync);

// Where Fusebox's webhook channel delivers alerts, so the demo shows them without a Slack account.
app.MapPost("/alerts", async (HttpRequest request, AlertSink sink) =>
{
    await sink.ReceiveAsync(request.Body, request.HttpContext.RequestAborted);
    return Results.Ok();
});
app.MapGet("/alerts", (AlertSink sink) => Results.Ok(sink.Recent()));

app.Run();
