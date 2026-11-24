using Fusebox.Application.Detection;

namespace Fusebox.Application.Diagnosis;

/// <summary>
/// Writes the explanation from the evidence with fixed wording. Always available, free, private, and exact, so an
/// alert never has to wait for or trust an LLM.
/// </summary>
public static class TemplateNarrator
{
    /// <summary>How much of the change the top factor must explain, at least, to be called a likely cause.</summary>
    private const double LikelyCauseShare = 0.5;

    /// <summary>The share above which a factor is worth mentioning.</summary>
    private const double WorthMentioning = 0.1;

    public static Narration Narrate(Evidence evidence, string? fallbackReason = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var top = evidence.TopMover;
        var sentences = new List<string> { Headline(evidence) };

        if (top is null)
        {
            sentences.Add("The change could not be traced to one cause from the data available.");
        }
        else
        {
            var factors = evidence.Factors.Where(f => Math.Abs(f.ShareOfChange!.Value) >= WorthMentioning).Take(2).ToList();
            sentences.Add(factors.Count == 0 ? Describe(top, "The biggest mover was") : DescribeAll(factors));
        }

        if (evidence.Items.FirstOrDefault(i => i.Kind == EvidenceKind.Segment) is { } segment && top is not null && top.Kind == EvidenceKind.Factor)
        {
            sentences.Add(DescribeSegment(segment));
        }

        if (evidence.IsThin)
        {
            sentences.Add("There was little data to go on, so treat this with caution.");
        }

        return new Narration(
            string.Join(' ', sentences),
            Confidence(evidence),
            CitedIds(evidence),
            Remediation(top?.Id ?? (evidence.Items.Count > 0 ? evidence.Items[0].Id : null)),
            NarrationSource.Template,
            FallbackReason: fallbackReason);
    }

    /// <summary>
    /// How far the evidence supports an explanation, from the evidence alone: high when one factor clearly accounts for
    /// nearly all of it, low when there is little data or the cause is unclear.
    /// </summary>
    public static NarrationConfidence Confidence(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var share = Math.Abs(evidence.TopMover?.ShareOfChange ?? 0);

        if (evidence.IsThin || share < LikelyCauseShare)
        {
            return NarrationConfidence.Low;
        }

        return share >= 0.8 && Math.Abs(evidence.Unexplained) < 0.1 ? NarrationConfidence.High : NarrationConfidence.Medium;
    }

    // The headline, the factors worth mentioning, and the slice of traffic that was described. Not factors that did not move.
    private static List<string> CitedIds(Evidence evidence)
    {
        var ids = new List<string>();

        if (evidence.Items.FirstOrDefault(i => i.Kind == EvidenceKind.Context) is { } headline)
        {
            ids.Add(headline.Id);
        }

        ids.AddRange(evidence.Factors.Where(f => Math.Abs(f.ShareOfChange!.Value) >= WorthMentioning).Take(2).Select(f => f.Id));

        if (evidence.TopMover is { } top && !ids.Contains(top.Id))
        {
            ids.Add(top.Id);
        }

        if (evidence.TopMover?.Kind == EvidenceKind.Factor && evidence.Items.FirstOrDefault(i => i.Kind == EvidenceKind.Segment) is { } segment)
        {
            ids.Add(segment.Id);
        }

        return ids;
    }

    private static string Headline(Evidence evidence)
    {
        var headline = evidence.Items.FirstOrDefault(i => i.Kind == EvidenceKind.Context);
        var name = AnomalyDescription.Name(evidence.Signal);

        return headline is { Before: { } before, Now: { } now, Unit: var unit } && headline.ChangeRatio is { } ratio
            ? $"{name} rose {EvidenceFormat.Change(ratio)}, from {EvidenceFormat.Value(unit, before)} to {EvidenceFormat.Value(unit, now)}."
            : $"{name} jumped.";
    }

    private static string DescribeAll(IReadOnlyList<EvidenceItem> factors) =>
        string.Join(
            ' ',
            factors.Select((factor, index) => Describe(factor, index == 0 ? "The biggest mover was" : "Also")));

    private static string Describe(EvidenceItem item, string lead)
    {
        var share = item.ShareOfChange is { } s ? $" ({EvidenceFormat.Share(s)} of the change)" : string.Empty;
        var values = item.Before is { } before && item.Now is { } now
            ? $": {EvidenceFormat.Value(item.Unit, before)} -> {EvidenceFormat.Value(item.Unit, now)}"
            : string.Empty;
        var detail = string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : $" [{item.Detail}]";

        return $"{lead} {item.Label.ToLowerInvariant()}{values}{share}{detail}.";
    }

    private static string DescribeSegment(EvidenceItem segment)
    {
        var share = segment.ShareOfChange is { } s ? $" ({EvidenceFormat.Share(s)} of the change)" : string.Empty;
        var values = segment.Before is { } before && segment.Now is { } now
            ? $" went from {EvidenceFormat.Value(segment.Unit, before)} to {EvidenceFormat.Value(segment.Unit, now)}"
            : " moved";

        return $"{segment.Label}{values}{share}.";
    }

    private static string Remediation(string? id) => id switch
    {
        null => "Compare recent deploys and configuration changes with when this started.",
        var i when i.StartsWith("spend.volume", StringComparison.Ordinal) || i.StartsWith("volume.", StringComparison.Ordinal) =>
            "Look for a retry loop, a runaway job or a caller with no rate limit, and consider a per-tenant limit.",
        var i when i.Contains("inputTokens", StringComparison.Ordinal) =>
            "Check whether prompts or conversation history are growing without bound, for example a truncation that stopped working after a deploy.",
        var i when i.Contains("outputTokens", StringComparison.Ordinal) =>
            "Check for a missing output cap (max_tokens) or a prompt change that makes answers much longer.",
        var i when i.EndsWith("modelMix", StringComparison.Ordinal) =>
            "Check whether a feature or configuration change moved traffic to a different model.",
        "spend.pricePerToken" =>
            "Check the price catalog against the vendor's current pricing. It can also reflect longer outputs, which cost more per token.",
        "spend.costPerCall" =>
            "Check what changed in how this API is billed, such as a plan, a tier or a per-call price.",
        var i when i.StartsWith("repeats.", StringComparison.Ordinal) =>
            "Look for a client retrying the same call in a loop, or a missing cache or idempotency key. Backing off between retries, and capping them, usually fixes it.",
        var i when i.StartsWith("errors.", StringComparison.Ordinal) =>
            "Check the vendor's status page and recent deploys, and see which model is failing.",
        var i when i.StartsWith("latency.", StringComparison.Ordinal) || i.StartsWith("model.", StringComparison.Ordinal) =>
            "Check the vendor's status and whether answers have become longer, and consider a timeout and a circuit breaker.",
        _ => "Compare recent deploys and configuration changes with when this started.",
    };
}
