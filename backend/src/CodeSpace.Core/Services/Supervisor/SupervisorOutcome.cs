using System.Text.Json;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The canonical SHAPE of a supervisor decision's recorded outcome JSON (PR-E E3) and the per-turn-per-spawn
/// AgentRun wait key (must-fix #1). Co-located with the turn concern: the executor WRITES these outcomes, the
/// turn service READS the staged-agent count from a replayed spawn/retry outcome (to re-classify the suspend
/// path on a Duplicate replay), and the executor stamps the wait IterationKey. A pure helper — no DB, no state.
///
/// <para>The spawn/retry outcome records its staged <c>agentRunIds</c> so a replay re-derives the SAME
/// park-on-K-agents classification WITHOUT re-staging, and a later <c>merge</c> can read the prior Attempt's
/// agent results by id. The <c>agentCount</c> field is the count the node parks on.</para>
/// </summary>
public static class SupervisorOutcome
{
    /// <summary>The wait IterationKey for the k-th agent a spawn/retry staged at turn N: <c>&lt;nodeId&gt;#turn{N}#{k}</c> (must-fix #1's full form, mirroring flow.map's <c>&lt;mapId&gt;#&lt;i&gt;</c>). Distinct per (turn, spawn-index) so K waits never collide.</summary>
    public static string AgentWaitKey(string nodeId, int turnNumber, int spawnIndex) => $"{nodeId}#turn{turnNumber}#{spawnIndex}";

    /// <summary>Read the count of agent runs a recorded spawn/retry outcome staged (0 when the outcome has no <c>agentCount</c> — a synchronous verb's outcome). Best-effort: a malformed / absent field reads 0.</summary>
    public static int ReadStagedAgentCount(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return 0;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("agentCount", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>Read the agent-run ids a recorded spawn/retry outcome staged, in spawn order. Empty when absent/malformed. Used by <c>merge</c> to read prior Attempt results by id.</summary>
    public static IReadOnlyList<Guid> ReadStagedAgentRunIds(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return Array.Empty<Guid>();

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("agentRunIds", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<Guid>();

            return arr.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out var id) ? id : (Guid?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }
}
