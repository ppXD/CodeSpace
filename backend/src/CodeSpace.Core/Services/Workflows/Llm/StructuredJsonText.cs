using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Shared helpers for the structured-output FALLBACK path: not every model / gateway honours forced
/// tool/function calling — many return the requested JSON as free-text message CONTENT instead. These let both the
/// Anthropic and OpenAI clients (a) steer the model to emit JSON via a system-prompt instruction, and (b) recover a
/// JSON object out of a text reply (stripping markdown fences + surrounding prose) when no tool call came back. This
/// is what makes structured output GENERIC across models, not just the tool-calling ones.
/// </summary>
internal static class StructuredJsonText
{
    /// <summary>Append a "respond with ONLY this JSON" instruction (carrying the schema) to the system prompt, so a model that ignores the forced tool still knows the exact shape to return.</summary>
    public static string WithSchemaInstruction(string systemPrompt, JsonElement schema)
    {
        var instruction = "Respond with a SINGLE JSON object that conforms to this JSON Schema. Output ONLY the JSON object — no prose, no explanation, no markdown code fences:\n" + schema.GetRawText();

        return string.IsNullOrWhiteSpace(systemPrompt) ? instruction : systemPrompt + "\n\n" + instruction;
    }

    /// <summary>Augment the base system prompt with the validation errors from a previous attempt so the RE-ASK names exactly what to fix — the model that returned a shape-invalid object gets a precise correction, not just a blind retry.</summary>
    public static string WithValidationFeedback(string systemPrompt, IReadOnlyList<string> errors, JsonElement previous)
    {
        var feedback =
            "Your previous response did NOT conform to the required JSON Schema. Fix exactly these problems and respond again with ONLY the corrected JSON object:\n" +
            string.Join("\n", errors.Select(e => "- " + e)) +
            "\n\nYour previous (invalid) response was:\n" + previous.GetRawText();

        return string.IsNullOrWhiteSpace(systemPrompt) ? feedback : systemPrompt + "\n\n" + feedback;
    }

    /// <summary>
    /// Recover a JSON object from a model's free-text reply — the fallback when it returned the JSON as content
    /// instead of a tool/function call. Strips a leading <c>```json</c> / trailing <c>```</c> fence, then takes the
    /// FIRST BALANCED <c>{ … }</c> span (brace-depth + string-aware, so a brace inside a string or trailing prose
    /// doesn't break it) and parses it. Null when the content holds no complete JSON object (incl. a truncated one).
    /// </summary>
    public static JsonElement? TryExtractObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var span = ExtractFirstBalancedObject(StripFences(content));
        if (span is null) return null;

        try { return JsonDocument.Parse(span).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    /// <summary>The first complete top-level <c>{ … }</c> by brace depth — ignoring braces inside strings (with escapes) and any prose / second object after it. Null when there is no opening brace or the object is unterminated (truncated output).</summary>
    private static string? ExtractFirstBalancedObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text.Substring(start, i - start + 1);
        }

        return null;
    }

    /// <summary>A short, single-line preview of a model reply for a diagnostic exception (so a CI failure shows WHAT the model actually returned, not just "no structured output"). Truncated + newline-collapsed.</summary>
    public static string Preview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return "(empty)";

        var collapsed = content.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return collapsed.Length <= 600 ? collapsed : collapsed[..600] + "…";
    }

    private static string StripFences(string content)
    {
        var text = content.Trim();

        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline >= 0) text = text[(firstNewline + 1)..];   // drop the ```json (or ```) opening line

        if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];

        return text.Trim();
    }
}
