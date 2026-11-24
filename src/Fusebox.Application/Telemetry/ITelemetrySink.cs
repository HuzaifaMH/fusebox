namespace Fusebox.Application.Telemetry;

/// <summary>
/// Port: where a finished request's record goes. Taking a record must never make the request wait.
/// </summary>
public interface ITelemetrySink
{
    /// <summary>
    /// Hands the record over and returns at once. Returns false if it was dropped because the sink is full.
    /// Never blocks, never throws.
    /// </summary>
    bool TryRecord(RequestRecord record);
}
