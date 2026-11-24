using System.Collections.Concurrent;
using System.Text.Json;

namespace Fusebox.Demo;

/// <summary>Receives alerts from Fusebox's webhook channel, writes them to the log and keeps the latest few.</summary>
internal sealed partial class AlertSink(ILogger<AlertSink> logger)
{
    private const int Capacity = 50;

    private readonly ConcurrentQueue<JsonElement> _recent = new();
    private readonly ILogger<AlertSink> _logger = logger;

    public async Task ReceiveAsync(Stream body, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var root = document.RootElement.Clone();

        _recent.Enqueue(root);
        while (_recent.Count > Capacity && _recent.TryDequeue(out _))
        {
        }

        var alert = root.GetProperty("alert");
        LogAlert(
            root.GetProperty("event").GetString() ?? "unknown",
            alert.GetProperty("severity").GetString() ?? "unknown",
            alert.GetProperty("title").GetString() ?? string.Empty,
            alert.GetProperty("summary").GetString() ?? string.Empty);
    }

    public IReadOnlyList<JsonElement> Recent() => [.. _recent.Reverse()];

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "ALERT [{Event}] {Severity}: {Title}. {Summary}")]
    private partial void LogAlert(string @event, string severity, string title, string summary);
}
