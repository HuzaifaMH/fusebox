using System.Globalization;

namespace Fusebox.Application.Diagnosis;

/// <summary>Writes evidence values the way a person would say them, in the item's unit.</summary>
public static class EvidenceFormat
{
    public static string Value(string unit, double value)
    {
        var culture = CultureInfo.InvariantCulture;

        return unit switch
        {
            "$/min" => (value is > 0 and < 0.01 ? "$" + value.ToString("0.0000", culture) : "$" + value.ToString("#,0.00", culture)) + "/min",
            "$/call" => "$" + value.ToString("0.0000", culture) + "/call",
            "calls/min" => value.ToString(value < 10 ? "0.#" : "#,0", culture) + " calls/min",
            "tokens/call" => value.ToString("#,0", culture) + " tokens/call",
            "%" => value.ToString(value < 10 ? "0.#" : "0", culture) + "%",
            "ms" => value.ToString("#,0", culture) + " ms",
            _ => value.ToString("#,0.##", culture),
        };
    }

    /// <summary>A change as a multiple, such as "6x", or as a percentage when it is small, such as "+40%".</summary>
    public static string Change(double ratio)
    {
        var culture = CultureInfo.InvariantCulture;

        return ratio >= 2 ? ratio.ToString(ratio < 10 ? "0.#" : "0", culture) + "x" : (ratio - 1).ToString("+0%;-0%", culture);
    }

    /// <summary>A share of a change, such as "87%".</summary>
    public static string Share(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
