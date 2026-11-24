using System.Collections.Concurrent;
using Fusebox.Application.Telemetry;

namespace Fusebox.Gateway.Tests;

/// <summary>Keeps every record it is given, and can pretend to be full.</summary>
internal sealed class RecordingTelemetrySink : ITelemetrySink
{
    private readonly ConcurrentQueue<RequestRecord> _records = new();

    /// <summary>When false, records are refused, as a full queue would refuse them.</summary>
    public bool Accepting { get; set; } = true;

    public IReadOnlyCollection<RequestRecord> Records => _records;

    public bool TryRecord(RequestRecord record)
    {
        if (!Accepting)
        {
            return false;
        }

        _records.Enqueue(record);
        return true;
    }
}
