namespace Fusebox.Application.Diagnosis;

/// <summary>How anomalies are investigated and explained.</summary>
public sealed class DiagnosisOptions
{
    public const string SectionName = "Fusebox:Diagnosis";

    /// <summary>Turns diagnosis on. Off by default, because it needs anomaly detection and the per-minute totals.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often new anomalies are picked up for diagnosis.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many minutes before the anomaly window are compared with it.</summary>
    public int BeforeMinutes { get; set; } = 60;

    /// <summary>With fewer minutes of data than this in the anomaly window, the evidence is marked thin.</summary>
    public int MinWindowMinutes { get; set; } = 3;

    /// <summary>With fewer minutes of data than this in the comparison period, the evidence is marked thin.</summary>
    public int MinBeforeMinutes { get; set; } = 20;

    /// <summary>With fewer requests than this in the anomaly window, the evidence is marked thin.</summary>
    public long MinRequests { get; set; } = 20;

    /// <summary>An anomaly that started longer ago than this is not diagnosed, since what it needed to compare has moved on.</summary>
    public TimeSpan MaxAnomalyAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The most anomalies diagnosed in one round.</summary>
    public int BatchSize { get; set; } = 5;

    /// <summary>How many slices of traffic, such as models, are listed as evidence.</summary>
    public int MaxSegments { get; set; } = 3;

    /// <summary>Whether an LLM is asked to put the evidence into words. Without it, a fixed template does.</summary>
    public bool NarratorEnabled { get; set; }

    /// <summary>A hard limit on LLM explanations per calendar month. Past it, the template is used.</summary>
    public int MaxNarrationsPerMonth { get; set; } = 300;

    /// <summary>How long the LLM has to answer before the template is used instead.</summary>
    public TimeSpan NarratorTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
