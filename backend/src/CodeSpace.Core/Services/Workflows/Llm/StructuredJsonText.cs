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

    /// <summary>
    /// Recover a JSON object from a model's free-text reply — the fallback when it returned the JSON as content
    /// instead of a tool/function call. Strips a leading <c>```json</c> / trailing <c>```</c> fence, then takes the
    /// outermost <c>{ … }</c> span and parses it. Null when the content holds no JSON object.
    /// </summary>
    public static JsonElement? TryExtractObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var text = StripFences(content);

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start) return null;

        try { return JsonDocument.Parse(text.Substring(start, end - start + 1)).RootElement.Clone(); }
        catch (JsonException) { return null; }
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
