using System.Text.Json;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// The ONE place a prior turn's CLEAN text is read from its persisted run (Rule 7) — shared by the digest builder
/// (<see cref="SessionContextBuilder"/>, which renders the recent verbatim turns) and the summarizer
/// (<c>SessionSummarizer</c>, which renders the older scrolled-out turns as distillation input). Reading the result
/// in one place keeps both halves of the thread context on the SAME source-of-truth + projection-shape contract.
/// </summary>
internal static class SessionTurnText
{
    /// <summary>Clip each turn's rendered result so one verbose turn can't blow up the prompt (or the distillation input).</summary>
    internal const int MaxResultChars = 600;

    /// <summary>
    /// A turn's result text, read GENERICALLY across projection shapes from its declared <c>OutputsJson</c>: a
    /// single-agent terminal surfaces <c>summary</c>, plan-map <c>combined</c>, supervisor <c>reason</c> — first
    /// present wins. A new projection surfacing its result under another key adds it to THIS fallback chain.
    /// </summary>
    internal static string? ReadResult(string outputsJson) =>
        ReadString(outputsJson, "summary") ?? ReadString(outputsJson, "combined") ?? ReadString(outputsJson, "reason");

    /// <summary>Read a non-blank string field from a JSON object, tolerating malformed / non-object / absent payloads (returns null).</summary>
    internal static string? ReadString(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Clip(string s) => s.Length <= MaxResultChars ? s : s[..MaxResultChars] + "…";
}
