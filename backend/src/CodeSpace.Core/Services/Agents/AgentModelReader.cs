using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Extracts the model the CLI ACTUALLY ran from a run's normalized events — the generic, tolerant primitive that
/// populates <see cref="AgentRunResult.Model"/>. Each harness names it on a different leading event (Claude's
/// <c>init</c> line, Codex's <c>thread.started</c> / <c>turn.started</c>), but all put it under a <c>model</c> key, so
/// this scans the structured payload of each event (also under the <c>msg</c> envelope Codex has used) for that key and
/// returns the FIRST one — a run's model is constant, so the earliest carrier is it. Returns null when no event carried
/// one (a pre-model CLI, or a stream that never reached its config line) rather than fabricating one. Pure + stateless,
/// mirroring <see cref="AgentSessionIdReader"/> — harness-agnostic by construction, so a new harness needs no change
/// here as long as its stream names the model <c>model</c>.
/// </summary>
public static class AgentModelReader
{
    // Model keys across the harnesses that surface one — a stream is from a single harness, so checking each is safe.
    private static readonly string[] ModelKeys = { "model", "model_name" };

    /// <summary>Scan events in emission order and return the FIRST recognizable model, or null when none carried one.</summary>
    public static string? TryRead(IReadOnlyList<AgentEvent> events)
    {
        foreach (var e in events)
            if (e.Data is { } data && TryReadFrom(data, out var model)) return model;

        return null;
    }

    /// <summary>Read a non-empty model from one structured payload — the payload itself, then the <c>msg</c> envelope.</summary>
    private static bool TryReadFrom(JsonElement data, out string model)
    {
        model = "";

        if (data.ValueKind != JsonValueKind.Object) return false;

        if (TryReadKeys(data, out model)) return true;

        return data.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object && TryReadKeys(msg, out model);
    }

    private static bool TryReadKeys(JsonElement obj, out string model)
    {
        foreach (var key in ModelKeys)
            if (obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
            {
                model = s;
                return true;
            }

        model = "";
        return false;
    }
}
