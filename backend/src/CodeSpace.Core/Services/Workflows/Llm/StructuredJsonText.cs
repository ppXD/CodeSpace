using System.Linq;
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

    /// <summary>Augment the base system prompt after a PARSE failure (the previous reply wasn't valid JSON at all — not merely schema-invalid) so the RE-ASK names the fault + shows what was returned. The correction is precise: a single complete, valid, fully-closed JSON object and nothing else.</summary>
    public static string WithMalformedFeedback(string systemPrompt, string? previousError)
    {
        var feedback =
            "Your previous response was NOT valid JSON and could not be parsed. Respond AGAIN with a SINGLE, COMPLETE, valid JSON object and NOTHING else — no prose, no markdown fences, no trailing text. Make sure every string and every bracket is properly closed." +
            (string.IsNullOrWhiteSpace(previousError) ? "" : "\n\nThe parser reported:\n" + previousError);

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

        var text = StripFences(content);

        // Strict: the first COMPLETE balanced { … }, parsed verbatim. The common, happy path.
        var span = ExtractFirstBalancedObject(text);
        if (span is not null)
        {
            try { return JsonDocument.Parse(span).RootElement.Clone(); }
            catch (JsonException) { /* malformed even though balanced — fall through to repair */ }
        }

        // Repair: recover a TRUNCATED / trailing-broken object (a long output cut at the token cap is the #1 cause).
        // Runs ONLY when the strict path failed, and the recovered object still faces schema validation downstream —
        // so repair can never turn a working case bad; it only rescues a near-valid object from a hard Malformed fault.
        return TryRepair(text);
    }

    /// <summary>
    /// Best-effort recovery of a truncated / trailing-broken JSON object: scan from the first <c>{</c> tracking the
    /// open-bracket stack + string state, close an unterminated string, trim a dangling trailing tail (a stray comma or
    /// a <c>"key":</c> with no value), then close every still-open <c>[</c>/<c>{</c>. Parses the repaired candidate;
    /// null when it still doesn't parse (repair never invents data — an unrecoverable reply stays a clean failure).
    /// </summary>
    private static JsonElement? TryRepair(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var chars = new List<char>();
        var stack = new List<char>();   // the still-open '{' / '[', outermost first
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            chars.Add(c);

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{' || c == '[') stack.Add(c);
            else if (c == '}' || c == ']')
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0) break;   // a complete top-level object closed (strict already tried it; harmless)
            }
        }

        if (inString) chars.Add('"');   // close an unterminated (truncated) string

        TrimDanglingTail(chars);

        for (var j = stack.Count - 1; j >= 0; j--)
            chars.Add(stack[j] == '{' ? '}' : ']');   // close still-open structures, innermost first

        try { return JsonDocument.Parse(new string(chars.ToArray())).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    /// <summary>Drop a dangling trailing tail that would break the close: whitespace, a trailing comma, and a <c>"key":</c> with no value (colon → drop the key string → drop the preceding comma) — repeatedly, until a complete token / opening bracket remains.</summary>
    private static void TrimDanglingTail(List<char> chars)
    {
        while (true)
        {
            TrimTrailingWhitespace(chars);
            if (chars.Count == 0) return;

            var last = chars[^1];

            if (last == ',') { chars.RemoveAt(chars.Count - 1); continue; }   // a trailing comma before the close

            if (last == ':')                                                 // a dangling `"key":` with no value
            {
                chars.RemoveAt(chars.Count - 1);   // drop ':'
                TrimTrailingWhitespace(chars);
                DropTrailingStringToken(chars);    // drop the "key"
                continue;                          // loop to shed the now-trailing comma
            }

            return;   // a complete value / opening brace — nothing to trim
        }
    }

    private static void TrimTrailingWhitespace(List<char> chars)
    {
        while (chars.Count > 0 && char.IsWhiteSpace(chars[^1])) chars.RemoveAt(chars.Count - 1);
    }

    /// <summary>Drop a complete trailing <c>"…"</c> string token (the closing quote back to its unescaped opening quote). No-op when the tail isn't a closed string.</summary>
    private static void DropTrailingStringToken(List<char> chars)
    {
        if (chars.Count == 0 || chars[^1] != '"') return;

        chars.RemoveAt(chars.Count - 1);   // drop the closing "

        while (chars.Count > 0)
        {
            var c = chars[^1];
            chars.RemoveAt(chars.Count - 1);

            if (c != '"') continue;

            var backslashes = 0;
            for (var k = chars.Count - 1; k >= 0 && chars[k] == '\\'; k--) backslashes++;
            if (backslashes % 2 == 0) return;   // an UNescaped quote → the opening quote → done
        }
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
