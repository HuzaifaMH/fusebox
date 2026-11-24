using System.Globalization;
using Fusebox.Domain;

namespace Fusebox.Application.Detection;

/// <summary>Says what an anomaly is in one plain sentence, with the numbers that triggered it.</summary>
public static class AnomalyDescription
{
    /// <summary>
    /// For example: "Spend per minute for acme on openai-chat is $4.10 versus a baseline of $1.00 (4.1x, threshold 4x),
    /// from 412 requests in the last 5 minutes."
    /// </summary>
    public static string Of(SeriesKey series, Finding finding, DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(options);

        var baselineWord = finding.Basis == BaselineBasis.Fixed ? "a fixed limit of" : "a baseline of";
        var basis = finding.Basis switch
        {
            BaselineBasis.HourOfWeek => "the same hour in earlier weeks",
            BaselineBasis.Recent => "the last few hours",
            _ => "no history yet",
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Name(finding.Signal)} for {series} is {Format(finding.Signal, finding.Current)} versus {baselineWord} {Format(finding.Signal, finding.Baseline)} " +
            $"({finding.Ratio:0.#}x, threshold {options.FireRatio:0.#}x, compared with {basis}), from {finding.WindowRequests} requests in the last {options.WindowMinutes} minutes.");
    }

    public static string Name(Signal signal) => signal switch
    {
        Signal.Volume => "Request volume per minute",
        Signal.Spend => "Spend per minute",
        Signal.ErrorRate => "Error rate",
        Signal.LatencyP95 => "Latency (95th percentile)",
        Signal.InputTokensPerRequest => "Input tokens per request",
        Signal.OutputTokensPerRequest => "Output tokens per request",
        Signal.RepeatRate => "Repeated requests",
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown signal."),
    };

    /// <summary>A value in the signal's own unit, such as "$4.10", "38%" or "4,200 ms".</summary>
    public static string Format(Signal signal, double value)
    {
        var culture = CultureInfo.InvariantCulture;

        return signal switch
        {
            Signal.Volume => $"{value.ToString("#,0.#", culture)} requests",
            Signal.Spend => "$" + Money.FromMicroDollars((long)Math.Round(value)).ToDollars().ToString("#,0.00", culture),
            Signal.ErrorRate or Signal.RepeatRate => $"{(value * 100).ToString("0.#", culture)}%",
            Signal.LatencyP95 => $"{value.ToString("#,0", culture)} ms",
            Signal.InputTokensPerRequest or Signal.OutputTokensPerRequest => $"{value.ToString("#,0", culture)} tokens",
            _ => value.ToString("0.##", culture),
        };
    }
}
