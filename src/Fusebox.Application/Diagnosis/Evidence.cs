using Fusebox.Application.Detection;

namespace Fusebox.Application.Diagnosis;

public enum EvidenceKind
{
    /// <summary>One of the parts a measurement is made of, such as calls per minute or tokens per call.</summary>
    Factor,

    /// <summary>One slice of the traffic, such as a model, and how much of the change it accounts for.</summary>
    Segment,

    /// <summary>A fact worth knowing that is not itself the cause, such as the headline change.</summary>
    Context,
}

/// <summary>One fact the engine found, with the numbers behind it.</summary>
/// <param name="Id">A stable name, such as "spend.inputTokens", that an explanation must cite.</param>
/// <param name="Kind">What sort of fact it is.</param>
/// <param name="Label">What it is, in words.</param>
/// <param name="Unit">The unit of <paramref name="Before"/> and <paramref name="Now"/>, such as "calls/min".</param>
/// <param name="Before">The value in the period before the anomaly, or null if it has no single value.</param>
/// <param name="Now">The value in the anomaly window.</param>
/// <param name="ShareOfChange">How much of the headline change this accounts for: 0.87 means 87%. Null if that does not apply.</param>
/// <param name="Detail">Extra words, such as which models moved.</param>
public sealed record EvidenceItem(
    string Id,
    EvidenceKind Kind,
    string Label,
    string Unit,
    double? Before,
    double? Now,
    double? ShareOfChange,
    string? Detail = null)
{
    /// <summary>The value now as a multiple of the value before, if both are known and the first is above zero.</summary>
    public double? ChangeRatio => Before is > 0 && Now is { } now ? now / Before : null;
}

/// <summary>What the engine found out about an anomaly, ranked by how much each thing moved.</summary>
/// <param name="Series">The tenant and route.</param>
/// <param name="Signal">What jumped.</param>
/// <param name="WindowStart">Start of the anomaly window.</param>
/// <param name="WindowEnd">End of the anomaly window.</param>
/// <param name="WindowMinutes">Length of the anomaly window.</param>
/// <param name="BeforeMinutes">Length of the period it is compared with.</param>
/// <param name="Items">The facts, biggest mover first.</param>
/// <param name="Unexplained">The share of the change that the facts do not account for, from 0 to 1.</param>
/// <param name="IsThin">True if there is too little data for the facts to be trusted much.</param>
public sealed record Evidence(
    SeriesKey Series,
    Signal Signal,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int WindowMinutes,
    int BeforeMinutes,
    IReadOnlyList<EvidenceItem> Items,
    double Unexplained,
    bool IsThin)
{
    /// <summary>
    /// What moved the most: the factor with the biggest share of the change (the "why"). Only if there are no factors does a
    /// slice of traffic, such as a model, stand in, since a slice says where the change was and not what caused it.
    /// Null if nothing could be measured.
    /// </summary>
    public EvidenceItem? TopMover => Factors.FirstOrDefault()
        ?? Items
            .Where(item => item.Kind == EvidenceKind.Segment && item.ShareOfChange is not null)
            .OrderByDescending(item => Math.Abs(item.ShareOfChange!.Value))
            .FirstOrDefault();

    /// <summary>The factors only, biggest mover first, leaving out slices of traffic.</summary>
    public IEnumerable<EvidenceItem> Factors => Items
        .Where(item => item.Kind == EvidenceKind.Factor && item.ShareOfChange is not null)
        .OrderByDescending(item => Math.Abs(item.ShareOfChange!.Value));
}
