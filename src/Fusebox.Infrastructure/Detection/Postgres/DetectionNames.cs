using Fusebox.Application.Detection;

namespace Fusebox.Infrastructure.Detection.Postgres;

/// <summary>The words stored in the database for signals and baseline bases. Stable, so queries can rely on them.</summary>
internal static class DetectionNames
{
    public static string Of(Signal signal) => signal switch
    {
        Signal.Volume => "volume",
        Signal.Spend => "spend",
        Signal.ErrorRate => "error_rate",
        Signal.LatencyP95 => "latency_p95",
        Signal.InputTokensPerRequest => "input_tokens_per_request",
        Signal.OutputTokensPerRequest => "output_tokens_per_request",
        Signal.RepeatRate => "repeat_rate",
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, "Unknown signal."),
    };

    public static Signal SignalOf(string name) => name switch
    {
        "volume" => Signal.Volume,
        "spend" => Signal.Spend,
        "error_rate" => Signal.ErrorRate,
        "latency_p95" => Signal.LatencyP95,
        "input_tokens_per_request" => Signal.InputTokensPerRequest,
        "output_tokens_per_request" => Signal.OutputTokensPerRequest,
        "repeat_rate" => Signal.RepeatRate,
        _ => throw new FormatException($"Unknown signal '{name}'."),
    };

    public static string Of(BaselineBasis basis) => basis switch
    {
        BaselineBasis.HourOfWeek => "hour_of_week",
        BaselineBasis.Recent => "recent",
        BaselineBasis.Fixed => "fixed",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Unknown basis."),
    };

    public static BaselineBasis BasisOf(string name) => name switch
    {
        "hour_of_week" => BaselineBasis.HourOfWeek,
        "recent" => BaselineBasis.Recent,
        "fixed" => BaselineBasis.Fixed,
        _ => throw new FormatException($"Unknown baseline basis '{name}'."),
    };
}
