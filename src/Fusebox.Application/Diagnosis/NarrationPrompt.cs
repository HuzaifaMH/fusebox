using System.Globalization;
using System.Text;
using System.Text.Json;
using Fusebox.Application.Detection;

namespace Fusebox.Application.Diagnosis;

/// <summary>
/// Builds what is sent to the LLM. It carries only the pre-aggregated evidence: numbers and labels such as model
/// names. Never prompts, responses, user ids or tenant names.
/// </summary>
public static class NarrationPrompt
{
    public const string SystemMessage =
        "You explain unusual API cost or reliability changes to an on-call engineer. " +
        "You are given measured evidence as JSON. Use only that evidence. Do not guess at causes it does not show, and do not invent numbers. " +
        "Retry rate is not measured, so do not mention retries as a finding. " +
        "Reply with a single JSON object and nothing else, with exactly these fields: " +
        "{\"diagnosis\": string, \"confidence\": \"low\" | \"medium\" | \"high\", \"evidenceIds\": [string], \"remediation\": string}. " +
        "diagnosis: at most three sentences saying what changed and the most likely cause. " +
        "evidenceIds: the ids of the evidence items the diagnosis relies on, copied exactly. " +
        "remediation: at most two sentences saying what to check or do. " +
        "Every number you write must appear in the evidence. If the evidence is thin or does not point to one cause, say so and choose low confidence.";

    public static LlmPrompt Build(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("signal", AnomalyDescription.Name(evidence.Signal));
            json.WriteString("route", evidence.Series.RouteId);
            json.WriteNumber("windowMinutes", evidence.WindowMinutes);
            json.WriteNumber("comparedWithMinutesBefore", evidence.BeforeMinutes);
            json.WriteBoolean("dataIsThin", evidence.IsThin);
            json.WriteNumber("unexplainedShare", Round(evidence.Unexplained));

            json.WriteStartArray("evidence");
            foreach (var item in evidence.Items)
            {
                json.WriteStartObject();
                json.WriteString("id", item.Id);
                json.WriteString("kind", item.Kind.ToString().ToLowerInvariant());
                json.WriteString("label", item.Label);

                if (item.Unit.Length > 0)
                {
                    json.WriteString("unit", item.Unit);
                }

                WriteOptional(json, "before", item.Before);
                WriteOptional(json, "now", item.Now);
                WriteOptional(json, "changeRatio", item.ChangeRatio);
                WriteOptional(json, "shareOfChange", item.ShareOfChange);

                if (!string.IsNullOrWhiteSpace(item.Detail))
                {
                    json.WriteString("detail", item.Detail);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return new LlmPrompt(SystemMessage, Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>A number to four significant figures, which is what the LLM is shown and so what it can be held to.</summary>
    public static double Round(double value) =>
        double.Parse(value.ToString("G4", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static void WriteOptional(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            json.WriteNumber(name, Round(number));
        }
    }
}
