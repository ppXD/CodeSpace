using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Extracts the model the CLI ACTUALLY ran from a run's normalized events — the primitive that populates
/// <see cref="AgentRunResult.Model"/>. It commits to no event type and to no spelling: it scans the payloads named by
/// the <see cref="AgentRunFactKeys"/> it is handed (the harness's own, via <see cref="IAgentHarnessRunFactKeys"/>, or
/// <see cref="AgentRunFactKeys.Fallback"/> for one that declares none) and returns the FIRST hit — a run's model is
/// constant, so the earliest carrier is it. Null when no event named one, never a fabricated value. Pure + stateless,
/// mirroring <see cref="AgentSessionIdReader"/>.
///
/// <para>Today exactly one harness feeds it: Claude Code names the model on its <c>init</c> line. Codex's
/// <c>exec --json</c> stream names NO model at all — see <c>CodexHarness.ReadSessionFrame</c>, which states why
/// (the model lives in a rollout's <c>turn_context</c>, a session-state file rather than a frame of this stream) —
/// so a Codex run's model reaches the result through <c>IAgentTranscriptModelSource</c>, never through here.</para>
/// </summary>
public static class AgentModelReader
{
    /// <summary>Scan events in emission order and return the FIRST recognizable model, or null when none carried one.</summary>
    public static string? TryRead(IReadOnlyList<AgentEvent> events, AgentRunFactKeys keys)
    {
        foreach (var e in events)
            if (TryRead(e, keys) is { } model) return model;

        return null;
    }

    /// <summary>The whole-list scan under <see cref="AgentRunFactKeys.Fallback"/> — for a caller holding a finished stream with no harness in hand (replay fixtures, the reader's own tests).</summary>
    public static string? TryRead(IReadOnlyList<AgentEvent> events) => TryRead(events, AgentRunFactKeys.Fallback);

    /// <summary>The per-event primitive: the model ONE event names, or null. <see cref="AgentRunFacts"/> keeps the first non-null so it never has to retain the stream.</summary>
    public static string? TryRead(AgentEvent normalized, AgentRunFactKeys keys)
    {
        if (normalized.Data is not { } data) return null;

        foreach (var container in AgentRunFactScan.Containers(data, keys.Envelopes))
            if (AgentRunFactScan.ReadString(container, keys.ModelKeys) is { } model) return model;

        return null;
    }
}
