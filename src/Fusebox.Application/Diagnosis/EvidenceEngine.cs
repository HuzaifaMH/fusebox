using System.Globalization;
using Fusebox.Application.Detection;

namespace Fusebox.Application.Diagnosis;

/// <summary>
/// Works out what changed when an anomaly fired, by comparing the anomaly window with the period before it.
/// Pure and deterministic: it only does arithmetic on the totals it is given, so every figure is exact and testable.
/// </summary>
/// <remarks>
/// Spend is a product: billable calls per minute, times tokens per call, times cost per token. The engine takes the
/// logarithm of each factor's change, which turns the product into a sum, so every factor's share of the increase is
/// exact and the shares add up to 100%. Tokens per call is split into input and output, and cost per token into a
/// shift of traffic between models and a change of price within a model. Other signals are split the same way where
/// they are ratios, and otherwise by which model or kind of request moved most.
/// </remarks>
public sealed class EvidenceEngine(DiagnosisOptions options)
{
    private const double MicroDollarsPerDollar = 1_000_000;
    private const string NoModel = "(no model)";

    public Evidence Analyze(
        SeriesKey series,
        Signal signal,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        PeriodTotals now,
        PeriodTotals before)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(before);

        var (items, unexplained) = signal switch
        {
            Signal.Spend => Spend(now, before),
            Signal.Volume => Volume(now, before),
            Signal.ErrorRate => RateSignal("errors", "Error rate", "%", 100, m => m.Errors, now, before),
            Signal.RepeatRate => RateSignal("repeats", "Share of calls repeating an earlier one", "%", 100, m => m.Repeats, now, before),
            Signal.LatencyP95 => Latency(now, before),
            Signal.InputTokensPerRequest => RateSignal("inputTokens", "Input tokens per call", "tokens/call", 1, m => m.InputTokens, now, before),
            Signal.OutputTokensPerRequest => RateSignal("outputTokens", "Output tokens per call", "tokens/call", 1, m => m.OutputTokens, now, before),
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown signal."),
        };

        var thin = now.Minutes < options.MinWindowMinutes || before.Minutes < options.MinBeforeMinutes || now.Requests < options.MinRequests;

        return new Evidence(
            series,
            signal,
            windowStart,
            windowEnd,
            now.PeriodMinutes,
            before.PeriodMinutes,
            Ranked(items),
            Math.Clamp(unexplained, -1, 1),
            thin);
    }

    // Spend = billable calls per minute x tokens per call x cost per token, and cost per token = model mix x effective price.
    // The effective price within a model also moves when the balance of input and output tokens does, since they are priced
    // differently, so it is reported as an effective cost and not as a list-price change.
    private (List<EvidenceItem> Items, double Unexplained) Spend(PeriodTotals now, PeriodTotals before)
    {
        var items = new List<EvidenceItem>();
        var cost0 = PerMinute(before.CostMicroUsd, before) / MicroDollarsPerDollar;
        var cost1 = PerMinute(now.CostMicroUsd, now) / MicroDollarsPerDollar;
        items.Add(new EvidenceItem("spend.total", EvidenceKind.Context, "Spend per minute", "$/min", cost0, cost1, null));

        if (cost0 <= 0 || cost1 <= 0 || cost1 == cost0 || before.Forwarded <= 0 || now.Forwarded <= 0)
        {
            return (items, 1);
        }

        var totalLog = Math.Log(cost1 / cost0);
        var volume0 = PerMinute(before.Forwarded, before);
        var volume1 = PerMinute(now.Forwarded, now);
        var volumeLog = Math.Log(volume1 / volume0);
        var perCallLog = totalLog - volumeLog;
        var explained = volumeLog / totalLog;

        items.Add(Factor("spend.volume", "Billable calls per minute", "calls/min", volume0, volume1, volumeLog / totalLog));

        var tokens0 = before.InputTokens + before.OutputTokens;
        var tokens1 = now.InputTokens + now.OutputTokens;

        if (tokens0 <= 0 || tokens1 <= 0)
        {
            // Tokens were not reported, as with a flat-priced API, so cost per call is the only thing left to say.
            items.Add(Factor(
                "spend.costPerCall",
                "Cost per call",
                "$/call",
                before.CostMicroUsd / MicroDollarsPerDollar / before.Forwarded,
                now.CostMicroUsd / MicroDollarsPerDollar / now.Forwarded,
                perCallLog / totalLog));
            explained += perCallLog / totalLog;
        }
        else
        {
            var perCall0 = tokens0 / (double)before.Forwarded;
            var perCall1 = tokens1 / (double)now.Forwarded;
            var tokenLog = Math.Log(perCall1 / perCall0);
            var priceLog = perCallLog - tokenLog;

            // Input and output add up to tokens per call, so the change is split in proportion to how much each grew.
            var input0 = before.InputTokens / (double)before.Forwarded;
            var input1 = now.InputTokens / (double)now.Forwarded;
            var deltaTokens = perCall1 - perCall0;
            var inputWeight = Math.Abs(deltaTokens) < 1e-9 ? 0.5 : (input1 - input0) / deltaTokens;
            var inputLog = tokenLog * inputWeight;
            var outputLog = tokenLog - inputLog;

            items.Add(Factor("spend.inputTokens", "Input tokens per call", "tokens/call", input0, input1, inputLog / totalLog));
            items.Add(Factor(
                "spend.outputTokens",
                "Output tokens per call",
                "tokens/call",
                before.OutputTokens / (double)before.Forwarded,
                now.OutputTokens / (double)now.Forwarded,
                outputLog / totalLog));

            var price = RateDecomposition.Decompose(
                Rates(before, m => m.CostMicroUsd, m => m.Tokens),
                Rates(now, m => m.CostMicroUsd, m => m.Tokens));

            double mixLog = 0;
            double rateLog = 0;
            if (price is not null && Math.Abs(price.Delta) > 1e-12)
            {
                mixLog = priceLog * price.MixEffect / price.Delta;
                rateLog = priceLog * price.RateEffect / price.Delta;
            }

            items.Add(Factor(
                "spend.modelMix",
                "Traffic shifting between models",
                string.Empty,
                null,
                null,
                mixLog / totalLog,
                price is null ? null : MixDetail(price, before, now, m => m.Tokens, "of tokens")));
            items.Add(Factor(
                "spend.pricePerToken",
                "Effective cost per token within a model",
                string.Empty,
                null,
                null,
                rateLog / totalLog,
                price is null ? null : PriceDetail(price, before, now) + ". Reflects a price change, or a shift between cheaper input tokens and pricier output tokens"));

            explained += (inputLog + outputLog + mixLog + rateLog) / totalLog;
        }

        var deltaCostPerMinute = (now.CostMicroUsd / (double)now.PeriodMinutes) - (before.CostMicroUsd / (double)before.PeriodMinutes);
        items.AddRange(ModelSegments(
            now,
            before,
            model => (Before: ModelPerMinute(before, model, m => m.CostMicroUsd), Now: ModelPerMinute(now, model, m => m.CostMicroUsd)),
            deltaCostPerMinute,
            "$/min",
            1 / MicroDollarsPerDollar,
            "spend"));

        AddRefusedContext(items, now, before);
        return (items, 1 - explained);
    }

    private (List<EvidenceItem> Items, double Unexplained) Volume(PeriodTotals now, PeriodTotals before)
    {
        var items = new List<EvidenceItem>();
        var requests0 = PerMinute(before.Requests, before);
        var requests1 = PerMinute(now.Requests, now);
        items.Add(new EvidenceItem("volume.total", EvidenceKind.Context, "Requests per minute", "calls/min", requests0, requests1, null));

        var delta = requests1 - requests0;
        if (Math.Abs(delta) < 1e-9)
        {
            return (items, 1);
        }

        // Every request is forwarded, refused, rejected or lost to an error, so these add up to the whole change.
        var parts = new (string Id, string Label, Func<PeriodTotals, long> Count)[]
        {
            ("volume.forwarded", "Calls sent to the vendor", p => p.Forwarded),
            ("volume.denied", "Calls refused for budget or price", p => p.Denied),
            ("volume.rejected", "Calls rejected as invalid", p => p.Rejected),
            ("volume.other", "Calls the gateway could not serve", p => p.Sum(m => m.Unavailable + m.Failed)),
        };

        var explained = 0.0;
        foreach (var (id, label, count) in parts)
        {
            var before0 = PerMinute(count(before), before);
            var now1 = PerMinute(count(now), now);

            if (before0 == 0 && now1 == 0)
            {
                continue;
            }

            var share = (now1 - before0) / delta;
            explained += share;
            items.Add(Factor(id, label, "calls/min", before0, now1, share));
        }

        items.AddRange(ModelSegments(
            now,
            before,
            model => (Before: ModelPerMinute(before, model, m => m.Requests), Now: ModelPerMinute(now, model, m => m.Requests)),
            delta,
            "calls/min",
            1,
            "volume"));

        AddErrorContext(items, now, before);
        return (items, 1 - explained);
    }

    // A ratio such as errors per forwarded call or tokens per forwarded call.
    private (List<EvidenceItem> Items, double Unexplained) RateSignal(
        string prefix,
        string label,
        string unit,
        double scale,
        Func<ModelTotals, long> numerator,
        PeriodTotals now,
        PeriodTotals before)
    {
        var items = new List<EvidenceItem>();
        var change = RateDecomposition.Decompose(
            Rates(before, m => numerator(m), m => m.Forwarded),
            Rates(now, m => numerator(m), m => m.Forwarded));

        if (change is null)
        {
            items.Add(new EvidenceItem($"{prefix}.rate", EvidenceKind.Context, label, unit, null, null, null));
            return (items, 1);
        }

        items.Add(new EvidenceItem($"{prefix}.rate", EvidenceKind.Context, label, unit, change.Before * scale, change.After * scale, null));

        if (Math.Abs(change.Delta) < 1e-12)
        {
            return (items, 1);
        }

        items.Add(Factor(
            $"{prefix}.withinModels",
            "The ratio changing within models",
            string.Empty,
            null,
            null,
            change.RateEffect / change.Delta,
            detail: $"{label} moved inside the same model"));
        items.Add(Factor(
            $"{prefix}.modelMix",
            "Traffic shifting between models",
            string.Empty,
            null,
            null,
            change.MixEffect / change.Delta,
            MixDetail(change, before, now, m => m.Forwarded, "of calls")));

        foreach (var segment in change.Segments.OrderByDescending(s => Math.Abs(s.Total)).Take(options.MaxSegments))
        {
            items.Add(new EvidenceItem(
                $"model.{segment.Key}",
                EvidenceKind.Segment,
                $"Model {Name(segment.Key)}",
                unit,
                ModelRatio(before, segment.Key, numerator) * scale,
                ModelRatio(now, segment.Key, numerator) * scale,
                segment.Total / change.Delta));
        }

        AddVolumeContext(items, now, before);
        return (items, 1 - ((change.RateEffect + change.MixEffect) / change.Delta));
    }

    private (List<EvidenceItem> Items, double Unexplained) Latency(PeriodTotals now, PeriodTotals before)
    {
        var items = new List<EvidenceItem>();
        var p95Before = RobustStatistics.LatencyPercentile(before.LatencyBuckets, 0.95);
        var p95Now = RobustStatistics.LatencyPercentile(now.LatencyBuckets, 0.95);
        items.Add(new EvidenceItem("latency.p95", EvidenceKind.Context, "95th percentile latency", "ms", p95Before, p95Now, null));

        // Each model's weight now times how much its own 95th percentile moved. Approximate, since percentiles do not add.
        var contributions = new List<(string Model, double Before, double Now, double Contribution)>();
        var nowForwarded = (double)Math.Max(now.Forwarded, 1);

        foreach (var (model, totals) in now.ByModel)
        {
            if (!before.ByModel.TryGetValue(model, out var earlier))
            {
                continue;
            }

            var after = RobustStatistics.LatencyPercentile(totals.LatencyBuckets, 0.95);
            var prior = RobustStatistics.LatencyPercentile(earlier.LatencyBuckets, 0.95);

            if (after is { } a && prior is { } p)
            {
                contributions.Add((model, p, a, totals.Forwarded / nowForwarded * (a - p)));
            }
        }

        var total = contributions.Sum(c => c.Contribution);
        foreach (var contribution in contributions.OrderByDescending(c => Math.Abs(c.Contribution)).Take(options.MaxSegments))
        {
            items.Add(new EvidenceItem(
                $"model.{contribution.Model}",
                EvidenceKind.Segment,
                $"Model {Name(contribution.Model)}",
                "ms",
                contribution.Before,
                contribution.Now,
                total > 0 ? contribution.Contribution / total : null,
                "Approximate share, from each model's own 95th percentile weighted by its calls"));
        }

        if (before.Forwarded > 0 && now.Forwarded > 0)
        {
            items.Add(new EvidenceItem(
                "context.outputTokens",
                EvidenceKind.Context,
                "Output tokens per call",
                "tokens/call",
                before.OutputTokens / (double)before.Forwarded,
                now.OutputTokens / (double)now.Forwarded,
                null,
                "Longer answers take longer"));
        }

        AddErrorContext(items, now, before);
        AddVolumeContext(items, now, before);
        return (items, 1);
    }

    private List<EvidenceItem> ModelSegments(
        PeriodTotals now,
        PeriodTotals before,
        Func<string, (double Before, double Now)> perMinute,
        double totalDelta,
        string unit,
        double scale,
        string prefix)
    {
        if (Math.Abs(totalDelta) < 1e-9)
        {
            return [];
        }

        return now.ByModel.Keys.Union(before.ByModel.Keys)
            .Select(model => (Model: model, Values: perMinute(model)))
            .Select(entry => (entry.Model, entry.Values, Share: (entry.Values.Now - entry.Values.Before) / totalDelta))
            .OrderByDescending(entry => Math.Abs(entry.Share))
            .Take(options.MaxSegments)
            .Select(entry => new EvidenceItem(
                $"model.{entry.Model}",
                EvidenceKind.Segment,
                $"Model {Name(entry.Model)}",
                unit,
                entry.Values.Before * scale,
                entry.Values.Now * scale,
                entry.Share,
                prefix == "spend" ? "Share of the increase in spend" : null))
            .ToList();
    }

    // The headline facts first, then the factors, then the slices of traffic, each with the biggest mover first.
    private static List<EvidenceItem> Ranked(List<EvidenceItem> items) =>
    [
        .. items.Where(item => item.Kind == EvidenceKind.Context),
        .. items.Where(item => item.Kind == EvidenceKind.Factor).OrderByDescending(item => Math.Abs(item.ShareOfChange ?? 0)),
        .. items.Where(item => item.Kind == EvidenceKind.Segment).OrderByDescending(item => Math.Abs(item.ShareOfChange ?? 0)),
    ];

    private static void AddRefusedContext(List<EvidenceItem> items, PeriodTotals now, PeriodTotals before)
    {
        var refused0 = PerMinute(before.Denied + before.Rejected, before);
        var refused1 = PerMinute(now.Denied + now.Rejected, now);

        if (refused0 > 0 || refused1 > 0)
        {
            items.Add(new EvidenceItem("context.refused", EvidenceKind.Context, "Calls refused or rejected per minute", "calls/min", refused0, refused1, null));
        }
    }

    private static void AddErrorContext(List<EvidenceItem> items, PeriodTotals now, PeriodTotals before)
    {
        if (before.Forwarded > 0 && now.Forwarded > 0)
        {
            items.Add(new EvidenceItem(
                "context.errorRate",
                EvidenceKind.Context,
                "Share of forwarded calls the vendor answered with an error",
                "%",
                before.Errors * 100.0 / before.Forwarded,
                now.Errors * 100.0 / now.Forwarded,
                null));
        }
    }

    private static void AddVolumeContext(List<EvidenceItem> items, PeriodTotals now, PeriodTotals before) =>
        items.Add(new EvidenceItem(
            "context.volume",
            EvidenceKind.Context,
            "Calls sent to the vendor per minute",
            "calls/min",
            PerMinute(before.Forwarded, before),
            PerMinute(now.Forwarded, now),
            null));

    private static EvidenceItem Factor(string id, string label, string unit, double? before, double? now, double? share, string? detail = null) =>
        new(id, EvidenceKind.Factor, label, unit, before, now, share, detail);

    private static string MixDetail(RateChange change, PeriodTotals before, PeriodTotals now, Func<ModelTotals, long> weight, string what)
    {
        var weightBefore = (double)Math.Max(before.Sum(weight), 1);
        var weightNow = (double)Math.Max(now.Sum(weight), 1);

        return string.Join(
            "; ",
            change.Segments
                .OrderByDescending(s => Math.Abs(s.MixEffect))
                .Take(2)
                .Select(s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Name(s.Key)}: {Percent(ShareOf(before, s.Key, weight) / weightBefore)} -> {Percent(ShareOf(now, s.Key, weight) / weightNow)} {what}")));
    }

    private static string PriceDetail(RateChange change, PeriodTotals before, PeriodTotals now) =>
        string.Join(
            "; ",
            change.Segments
                .OrderByDescending(s => Math.Abs(s.RateEffect))
                .Take(2)
                .Select(s => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Name(s.Key)}: ${ModelRatio(before, s.Key, m => m.CostMicroUsd, m => m.Tokens):0.00} -> ${ModelRatio(now, s.Key, m => m.CostMicroUsd, m => m.Tokens):0.00} per million tokens")));

    private static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static double ShareOf(PeriodTotals period, string model, Func<ModelTotals, long> weight) =>
        period.ByModel.TryGetValue(model, out var totals) ? weight(totals) : 0;

    private static double ModelRatio(PeriodTotals period, string model, Func<ModelTotals, long> numerator, Func<ModelTotals, long>? denominator = null)
    {
        if (!period.ByModel.TryGetValue(model, out var totals))
        {
            return 0;
        }

        var den = (denominator ?? (m => m.Forwarded))(totals);
        return den > 0 ? numerator(totals) / (double)den : 0;
    }

    private static Dictionary<string, SegmentRate> Rates(PeriodTotals period, Func<ModelTotals, double> numerator, Func<ModelTotals, double> denominator) =>
        period.ByModel.ToDictionary(pair => pair.Key, pair => new SegmentRate(numerator(pair.Value), denominator(pair.Value)));

    private static double PerMinute(long total, PeriodTotals period) => total / (double)Math.Max(period.PeriodMinutes, 1);

    private static double ModelPerMinute(PeriodTotals period, string model, Func<ModelTotals, long> selector) =>
        period.ByModel.TryGetValue(model, out var totals) ? PerMinute(selector(totals), period) : 0;

    private static string Name(string model) => model.Length == 0 ? NoModel : model;
}
