using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// P3.1a: extracts the harness-native session/thread id from a run's normalized events — the primitive that populates
/// <see cref="AgentRunResult.SessionId"/> (the handle a later rerun threads back to CONTINUE the prior CLI
/// conversation). Each harness's native stream names it differently — Claude carries <c>session_id</c> on its
/// <c>init</c> and <c>result</c> lines, Codex carries <c>thread_id</c> on its <c>thread.started</c> event — so WHERE
/// to look is not this reader's to know: it scans the payloads named by the <see cref="AgentRunFactKeys"/> it is
/// handed (the harness's own, via <see cref="IAgentHarnessRunFactKeys"/>, or
/// <see cref="AgentRunFactKeys.Fallback"/> for one that declares none) and returns null when none carried an id
/// rather than fabricating one. Pure + stateless, mirroring <see cref="AgentTokenUsageReader"/>; the id must have
/// been retained as <see cref="AgentEvent.Data"/> by the harness's parse to be visible here at all.
/// </summary>
public static class AgentSessionIdReader
{
    /// <summary>
    /// Scan events in emission order and return the FIRST recognizable session/thread id — for a run that's
    /// constant, so the first carrier (Codex's leading <c>thread.started</c>, Claude's <c>init</c> line) is the id.
    /// Null when no event carried one.
    /// </summary>
    public static string? TryRead(IReadOnlyList<AgentEvent> events, AgentRunFactKeys keys)
    {
        foreach (var e in events)
            if (TryRead(e, keys) is { } id) return id;

        return null;
    }

    /// <summary>The whole-list scan under <see cref="AgentRunFactKeys.Fallback"/> — for a caller holding a finished stream with no harness in hand (replay fixtures, the reader's own tests).</summary>
    public static string? TryRead(IReadOnlyList<AgentEvent> events) => TryRead(events, AgentRunFactKeys.Fallback);

    /// <summary>The per-event primitive: the id ONE event carries, or null. <see cref="AgentRunFacts"/> keeps the first non-null so it never has to retain the stream.</summary>
    public static string? TryRead(AgentEvent normalized, AgentRunFactKeys keys)
    {
        if (normalized.Data is not { } data) return null;

        foreach (var container in AgentRunFactScan.Containers(data, keys.Envelopes))
            if (AgentRunFactScan.ReadString(container, keys.SessionIdKeys) is { } id) return id;

        return null;
    }
}
