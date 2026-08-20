using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// D3b-i: extracts token usage from a run's normalized events — the primitive that populates
/// <see cref="AgentRunResult.TokenUsage"/> (the cost-accounting figure a per-team budget cap will consume). Each
/// harness's native stream reports usage differently — Codex under <c>usage</c> or
/// <c>info.total_token_usage.{input,output}_tokens</c>, Claude under <c>usage.{input,output}_tokens</c>, an
/// OpenAI-compatible gateway under <c>prompt_tokens</c>/<c>completion_tokens</c> — so WHERE to look is not this
/// reader's to know: it walks the containers named by the <see cref="AgentRunFactKeys"/> it is handed (the harness's
/// own, via <see cref="IAgentHarnessRunFactKeys"/>, or <see cref="AgentRunFactKeys.Fallback"/> for one that declares
/// none), takes the first that carries BOTH counts, and returns null when none did rather than guessing. Pure +
/// stateless.
/// </summary>
public static class AgentTokenUsageReader
{
    /// <summary>
    /// Scan events newest-first and return the LAST-emitted recognizable usage — for a cumulative-count
    /// stream (Codex emits a growing total per turn) that's the run total; for a single-usage stream
    /// (Claude's one result line) it's the only one. Null when no event carried a usage object.
    /// </summary>
    public static AgentTokenUsage? TryRead(IReadOnlyList<AgentEvent> events, AgentRunFactKeys keys)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (TryRead(events[i], keys) is { } usage) return usage;

        return null;
    }

    /// <summary>The whole-list scan under <see cref="AgentRunFactKeys.Fallback"/> — for a caller holding a finished stream with no harness in hand (replay fixtures, the reader's own tests).</summary>
    public static AgentTokenUsage? TryRead(IReadOnlyList<AgentEvent> events) => TryRead(events, AgentRunFactKeys.Fallback);

    /// <summary>
    /// The per-event primitive: the usage ONE event carries, or null. A candidate container matches only when BOTH
    /// figures are present, so a partial/unrelated object never yields a misleading half-count; the declared order
    /// puts a CUMULATIVE total ahead of a per-turn delta where a stream carries both. <see cref="AgentRunFacts"/>
    /// keeps the LAST non-null — the same figure the newest-first scan returns — so it never has to retain the stream.
    /// </summary>
    public static AgentTokenUsage? TryRead(AgentEvent normalized, AgentRunFactKeys keys)
    {
        if (normalized.Data is not { } data) return null;

        foreach (var container in AgentRunFactScan.Containers(data, keys.UsageContainers))
            if (TryReadCounts(container, keys, out var input, out var output)) return new AgentTokenUsage { InputTokens = input, OutputTokens = output };

        return null;
    }

    private static bool TryReadCounts(JsonElement obj, AgentRunFactKeys keys, out int input, out int output)
    {
        input = 0;
        output = 0;
        return obj.ValueKind == JsonValueKind.Object
               && AgentRunFactScan.TryReadInt(obj, keys.InputTokenKeys, out input)
               && AgentRunFactScan.TryReadInt(obj, keys.OutputTokenKeys, out output);
    }
}
