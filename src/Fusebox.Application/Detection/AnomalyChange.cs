namespace Fusebox.Application.Detection;

/// <summary>A change to an anomaly, to be saved.</summary>
public abstract record AnomalyChange
{
    private protected AnomalyChange()
    {
    }
}

/// <summary>A new anomaly has started.</summary>
public sealed record AnomalyOpenedChange(OpenAnomaly Anomaly) : AnomalyChange;

/// <summary>An anomaly goes on, or is calming. Peak and calm count are updated.</summary>
public sealed record AnomalyProgressedChange(Guid Id, double PeakValue, double PeakRatio, int CalmEvaluations, DateTimeOffset LastSeenAt) : AnomalyChange;

/// <summary>An anomaly has ended.</summary>
public sealed record AnomalyClosedChange(Guid Id, DateTimeOffset ClosedAt) : AnomalyChange;
