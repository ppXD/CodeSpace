using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// P3.1a: extracts the harness-native session/thread id from a run's normalized events — the generic, tolerant
/// primitive that populates <see cref="AgentRunResult.SessionId"/> (the handle a later rerun threads back to
/// CONTINUE the prior CLI conversation). Each harness's native stream names it differently — Claude carries
/// <c>session_id</c> on its result line, Codex carries <c>thread_id</c> on its <c>thread.started</c> event — so this
/// reader scans the structured payload of each event for either key (also under the <c>msg</c> envelope Codex has
/// used), and returns null when none is present rather than fabricating one. Pure + stateless, mirroring
/// <see cref="AgentTokenUsageReader"/>; the harness retains the id-bearing line's root as <see cref="AgentEvent.Data"/>,
/// so the id is already in the events the harness folds.
/// </summary>
public static class AgentSessionIdReader
{
    // The id keys, across the harnesses that surface one. The two never collide in one stream (a stream is from one
    // harness), so checking both is safe and keeps the reader harness-agnostic.
    private static readonly string[] IdKeys = { "session_id", "thread_id" };

    /// <summary>
    /// Scan events in emission order and return the FIRST recognizable session/thread id — for a run that's
    /// constant, so the first carrier (Codex's leading <c>thread.started</c>, Claude's result line) is the id.
    /// Null when no event carried one.
    /// </summary>
    public static string? TryRead(IReadOnlyList<AgentEvent> events)
    {
        foreach (var e in events)
            if (e.Data is { } data && TryReadFrom(data, out var id)) return id;

        return null;
    }

    /// <summary>Read a non-empty id from one structured payload — the payload itself, then the <c>msg</c> envelope.</summary>
    private static bool TryReadFrom(JsonElement data, out string id)
    {
        id = "";

        if (data.ValueKind != JsonValueKind.Object) return false;

        if (TryReadKeys(data, out id)) return true;

        return data.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object && TryReadKeys(msg, out id);
    }

    private static bool TryReadKeys(JsonElement obj, out string id)
    {
        foreach (var key in IdKeys)
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
            {
                id = s;
                return true;
            }

        id = "";
        return false;
    }
}
