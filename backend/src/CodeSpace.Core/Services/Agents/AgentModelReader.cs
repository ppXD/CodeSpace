using System.Text.Json;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Extracts the model the CLI ACTUALLY ran from a run's normalized events — the generic, tolerant primitive that
/// populates <see cref="AgentRunResult.Model"/>. It commits to no event type: it scans the structured payload of each
/// event (also under the <c>msg</c> envelope Codex has used) for one of <see cref="ModelKeys"/> and returns the FIRST
/// hit — a run's model is constant, so the earliest carrier is it. Returns null when no event carried one rather than
/// fabricating one. Pure + stateless, mirroring <see cref="AgentSessionIdReader"/>.
///
/// <para>Today exactly one harness feeds it: Claude Code names the model on its <c>init</c> line. Codex's
/// <c>exec --json</c> stream names NO model at all — see <c>CodexHarness.ReadSessionFrame</c>, which states why
/// (the model lives in a rollout's <c>turn_context</c>, a session-state file rather than a frame of this stream) —
/// so a Codex run's model reaches the result through <c>IAgentTranscriptModelSource</c>, never through here. The
/// tolerance is therefore forward-looking, not a second harness already served: a new harness is covered only if its
/// stream spells the model with one of <see cref="ModelKeys"/>, and one that spells it otherwise must extend that
/// table.</para>
/// </summary>
public static class AgentModelReader
{
    // The spellings this reader recognizes. A stream is from a single harness, so checking each is safe; a harness
    // that spells the model some other way is not read by this reader at all until its key is added here.
    private static readonly string[] ModelKeys = { "model", "model_name" };

    /// <summary>Scan events in emission order and return the FIRST recognizable model, or null when none carried one.</summary>
    public static string? TryRead(IReadOnlyList<AgentEvent> events)
    {
        foreach (var e in events)
            if (TryRead(e) is { } model) return model;

        return null;
    }

    /// <summary>The per-event primitive: the model ONE event names, or null. <see cref="AgentRunFacts"/> keeps the first non-null so it never has to retain the stream.</summary>
    public static string? TryRead(AgentEvent normalized) => normalized.Data is { } data && TryReadFrom(data, out var model) ? model : null;

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
