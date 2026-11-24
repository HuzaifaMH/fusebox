namespace Fusebox.Application.Detection;

/// <summary>
/// Port: told when an anomaly starts and ends, so someone can be alerted and its cause investigated.
/// </summary>
/// <remarks>
/// Methods run inside the detection run, so implementations should return quickly, for example by queueing the work.
/// An exception thrown here is logged and otherwise ignored.
/// </remarks>
public interface IAnomalyListener
{
    void AnomalyOpened(SeriesKey series, Finding finding, string description, DateTimeOffset at);

    void AnomalyClosed(OpenAnomaly anomaly, DateTimeOffset at);
}
