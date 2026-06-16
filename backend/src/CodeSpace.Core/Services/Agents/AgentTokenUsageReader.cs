using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// D3b-i: extracts token usage from a run's normalized events — the generic, tolerant primitive that
/// populates <see cref="AgentRunResult.TokenUsage"/> (the cost-accounting figure a per-team budget cap
/// will consume). Each harness's native stream reports usage differently — Codex (OpenAI) under
/// <c>info.total_token_usage.{input,output}_tokens</c>, Claude under <c>usage.{input,output}_tokens</c>,
/// an OpenAI-compatible gateway under <c>prompt_tokens</c>/<c>completion_tokens</c> — so this reader is
/// deliberately tolerant: it scans the structured payload of each event for a recognizable usage object,
/// across a bounded set of known nestings and key aliases, and returns null when none is present rather
/// than guessing. The harness decides which events to feed in (Codex: every event, taking the last
/// cumulative count; Claude: the final result line). Pure + stateless.
/// </summary>
public static class AgentTokenUsageReader
{
    // Aliases for the two figures, in priority order — Anthropic/Codex first, then the OpenAI-API spelling, then bare.
    private static readonly string[] InputKeys = { "input_tokens", "prompt_tokens", "input" };
    private static readonly string[] OutputKeys = { "output_tokens", "completion_tokens", "output" };

    /// <summary>
    /// Scan events newest-first and return the LAST-emitted recognizable usage — for a cumulative-count
    /// stream (Codex emits a growing total per turn) that's the run total; for a single-usage stream
    /// (Claude's one result line) it's the only one. Null when no event carried a usage object.
    /// </summary>
    public static AgentTokenUsage? TryRead(IReadOnlyList<AgentEvent> events)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (events[i].Data is { } data && TryReadFrom(data, out var usage)) return usage;

        return null;
    }

    /// <summary>
    /// Read input + output token counts from one structured payload. Checks a bounded list of known
    /// usage-object locations (the payload itself, then common nestings), preferring a CUMULATIVE total
    /// over a per-turn delta where both exist. A candidate matches only when BOTH figures are present, so
    /// a partial/unrelated object never yields a misleading half-count.
    /// </summary>
    private static bool TryReadFrom(JsonElement data, out AgentTokenUsage usage)
    {
        usage = default!;

        if (data.ValueKind != JsonValueKind.Object) return false;

        foreach (var candidate in UsageObjects(data))
            if (TryReadCounts(candidate, out var input, out var output))
            {
                usage = new AgentTokenUsage { InputTokens = input, OutputTokens = output };
                return true;
            }

        return false;
    }

    /// <summary>The bounded, ordered set of objects that may carry a usage block — cumulative locations first.</summary>
    private static IEnumerable<JsonElement> UsageObjects(JsonElement data)
    {
        yield return data;                                            // flat: {input_tokens, output_tokens}
        if (TryObject(data, "usage", out var usage)) yield return usage;   // Claude / OpenAI: {usage:{…}}

        if (TryObject(data, "info", out var info))
        {
            if (TryObject(info, "total_token_usage", out var total)) yield return total;   // Codex cumulative (preferred)
            yield return info;                                                              // Codex flat-on-info fallback
        }

        if (TryObject(data, "total_token_usage", out var topTotal)) yield return topTotal;  // Codex without the info wrapper

        if (TryObject(data, "msg", out var msg))                     // Codex's alternate {msg:{…}} envelope
        {
            if (TryObject(msg, "usage", out var msgUsage)) yield return msgUsage;
            yield return msg;
        }
    }

    private static bool TryReadCounts(JsonElement obj, out int input, out int output)
    {
        input = 0;
        output = 0;
        return obj.ValueKind == JsonValueKind.Object
               && TryReadInt(obj, InputKeys, out input)
               && TryReadInt(obj, OutputKeys, out output);
    }

    private static bool TryReadInt(JsonElement obj, string[] keys, out int value)
    {
        foreach (var key in keys)
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value))
                return true;

        value = 0;
        return false;
    }

    private static bool TryObject(JsonElement parent, string key, out JsonElement child)
    {
        if (parent.TryGetProperty(key, out child) && child.ValueKind == JsonValueKind.Object) return true;

        child = default;
        return false;
    }
}
