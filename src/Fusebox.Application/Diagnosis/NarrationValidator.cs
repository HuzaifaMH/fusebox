using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fusebox.Application.Diagnosis;

/// <summary>The outcome of checking an LLM's answer: either a narration to use, or the reason it was refused.</summary>
public sealed record NarrationCheck(Narration? Narration, string? Problem)
{
    public bool IsValid => Narration is not null;
}

/// <summary>
/// Holds an LLM's answer to the evidence it was given. Nothing the model says is used unless it passes every check:
/// the right shape, only evidence that exists, only numbers that are in the evidence, and no more confidence than
/// the evidence supports. An answer that fails is thrown away and the template is used.
/// </summary>
public static partial class NarrationValidator
{
    private const int MaxDiagnosisLength = 800;
    private const int MaxRemediationLength = 500;
    private const int MaxEvidenceIds = 10;

    // A number may differ from one in the evidence by this much, to allow for "about 6 times" and rounding.
    private const double Tolerance = 0.03;

    private static readonly string[] Fields = ["diagnosis", "confidence", "evidenceIds", "remediation"];

    public static NarrationCheck Validate(string reply, string model, Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!TryParse(reply, out var parsed, out var problem))
        {
            return Refuse(problem!);
        }

        var (diagnosis, confidenceText, ids, remediation) = parsed;

        if (diagnosis.Length is 0 or > MaxDiagnosisLength || remediation.Length is 0 or > MaxRemediationLength)
        {
            return Refuse("the diagnosis or remediation is empty or too long");
        }

        if (!Enum.TryParse<NarrationConfidence>(confidenceText, ignoreCase: true, out var claimed) || !Enum.IsDefined(claimed))
        {
            return Refuse($"'{confidenceText}' is not a confidence level");
        }

        if (ids.Count is 0 or > MaxEvidenceIds || ids.Distinct().Count() != ids.Count)
        {
            return Refuse("the evidence ids are missing, repeated or too many");
        }

        var known = evidence.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = ids.FirstOrDefault(id => !known.Contains(id));
        if (unknown is not null)
        {
            return Refuse($"it cites evidence '{unknown}', which was not provided");
        }

        var invented = FindInventedNumber($"{diagnosis} {remediation}", evidence);
        if (invented is not null)
        {
            return Refuse($"the number {invented} does not appear in the evidence");
        }

        // The model may be more cautious than the evidence allows for, never less.
        var confidence = (NarrationConfidence)Math.Min((int)claimed, (int)TemplateNarrator.Confidence(evidence));

        return new NarrationCheck(
            new Narration(diagnosis.Trim(), confidence, ids, remediation.Trim(), NarrationSource.Llm, model),
            null);
    }

    private static NarrationCheck Refuse(string problem) => new(null, problem);

    private static bool TryParse(string reply, out (string Diagnosis, string Confidence, List<string> Ids, string Remediation) parsed, out string? problem)
    {
        parsed = default;
        problem = null;

        try
        {
            using var document = JsonDocument.Parse(StripFences(reply));
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "the reply is not a JSON object";
                return false;
            }

            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != Fields.Length || Fields.Any(field => !names.Contains(field, StringComparer.Ordinal)))
            {
                problem = $"the reply must have exactly the fields {string.Join(", ", Fields)}";
                return false;
            }

            if (root.GetProperty("diagnosis").ValueKind != JsonValueKind.String
                || root.GetProperty("confidence").ValueKind != JsonValueKind.String
                || root.GetProperty("remediation").ValueKind != JsonValueKind.String
                || root.GetProperty("evidenceIds").ValueKind != JsonValueKind.Array
                || root.GetProperty("evidenceIds").EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            {
                problem = "a field has the wrong type";
                return false;
            }

            parsed = (
                root.GetProperty("diagnosis").GetString()!,
                root.GetProperty("confidence").GetString()!,
                [.. root.GetProperty("evidenceIds").EnumerateArray().Select(e => e.GetString()!)],
                root.GetProperty("remediation").GetString()!);
            return true;
        }
        catch (JsonException)
        {
            problem = "the reply is not valid JSON";
            return false;
        }
    }

    // Models sometimes wrap JSON in a code fence even when told not to.
    private static string StripFences(string reply)
    {
        var text = reply.Trim();

        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);

        return firstNewline > 0 && lastFence > firstNewline ? text[(firstNewline + 1)..lastFence].Trim() : text;
    }

    /// <summary>The first number in the text that is not in the evidence, or null if every number is.</summary>
    private static string? FindInventedNumber(string text, Evidence evidence)
    {
        var allowed = AllowedNumbers(evidence);

        // Model names carry digits, such as "gpt-4o", and are not claims about the data.
        foreach (var model in evidence.Items.Where(i => i.Id.StartsWith("model.", StringComparison.Ordinal)).Select(i => i.Id["model.".Length..]).Where(m => m.Length > 0))
        {
            text = text.Replace(model, "MODEL", StringComparison.OrdinalIgnoreCase);
        }

        foreach (Match match in NumberPattern().Matches(text))
        {
            if (!double.TryParse(match.Groups["number"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            // "$4.2k" and "1.2M" are 4,200 and 1,200,000. A multiplier such as "6x" or a percentage is the number itself.
            number *= match.Groups["suffix"].Value switch
            {
                "k" or "K" => 1_000,
                "m" or "M" => 1_000_000,
                "b" or "B" => 1_000_000_000,
                _ => 1,
            };

            if (!allowed.Any(value => Close(number, value)))
            {
                return match.Value;
            }
        }

        return null;
    }

    /// <summary>Every number a faithful explanation might quote, in every way it might be quoted.</summary>
    private static List<double> AllowedNumbers(Evidence evidence)
    {
        var numbers = new List<double> { evidence.WindowMinutes, evidence.BeforeMinutes, 95 };

        void Add(double? value, bool alsoAsPercent)
        {
            if (value is not { } number || !double.IsFinite(number))
            {
                return;
            }

            var shown = NarrationPrompt.Round(number);
            numbers.Add(shown);
            numbers.Add(Math.Abs(shown));

            if (alsoAsPercent)
            {
                numbers.Add(shown * 100);
                numbers.Add(Math.Abs(shown * 100));
            }
        }

        foreach (var item in evidence.Items)
        {
            // Values quoted as they are: "from $0.20 to $1.00", "10 calls/min". A percentage unit already is one.
            Add(item.Before, false);
            Add(item.Now, false);
            Add(item.ChangeRatio, false);

            // A change quoted as a percentage: 5x is also "400%" (up 400%) and "500%".
            if (item.ChangeRatio is { } ratio)
            {
                Add((ratio - 1) * 100, false);
                Add(ratio * 100, false);
            }

            Add(item.ShareOfChange, true);
        }

        return numbers;
    }

    private static bool Close(double number, double allowed) =>
        Math.Abs(number - allowed) <= Math.Max(Tolerance * Math.Abs(allowed), 0.051);

    // A number that stands on its own, with an optional suffix such as "x", "%", "k" or "ms". Not part of a name such as "4o".
    [GeneratedRegex(@"(?<![A-Za-z\d.])(?<number>\d[\d,]*(?:\.\d+)?)(?<suffix>ms|[kKmMbBxX%])?(?![A-Za-z\d])")]
    private static partial Regex NumberPattern();
}
