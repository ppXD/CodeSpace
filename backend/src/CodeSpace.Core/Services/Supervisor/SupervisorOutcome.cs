using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

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

    /// <summary>The SupervisorDecision self-advance wait IterationKey a synchronous (plan/merge) turn parks on: <c>&lt;nodeId&gt;#turn{N}</c> (must-fix #1; the per-turn root the spawn key's <c>#{k}</c> + ask key's <c>#ask</c> hang off). Distinct per turn so each turn's self-advance row never collides.</summary>
    public static string SelfAdvanceWaitKey(string nodeId, int turnNumber) => $"{nodeId}#turn{turnNumber}";

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

    /// <summary>The Action wait IterationKey an ask_human turn parks on: <c>&lt;nodeId&gt;#turn{N}#ask</c> (mirroring the spawn key's <c>#turn{N}#{k}</c> shape). Distinct per turn so a later ask_human turn never collides with this one.</summary>
    public static string HumanWaitKey(string nodeId, int turnNumber) => $"{nodeId}#turn{turnNumber}#ask";

    /// <summary>Read the human-wait correlation token a recorded ask_human outcome posted its question card on (null when the outcome has none — a non-ask_human verb, or an ask_human that degraded to a no-surface stop). A replay re-derives the SAME park-on-human classification + token WITHOUT re-posting.</summary>
    public static string? ReadHumanWaitToken(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return null;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("askHumanToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Read the question an ask_human outcome asked (null when absent/malformed) — folded with the recorded answer into the next turn's context so the decider sees "you asked X".</summary>
    public static string? ReadAskHumanQuestion(string? outcomeJson) => ReadStringField(outcomeJson, "question");

    /// <summary>
    /// Build an ask_human outcome JSON from its parts — the question, the question-card wait token, and the
    /// human's answer (null until answered). The single canonical shape the executor records on first execution
    /// (answer null) and the rehydrate FOLD re-stamps once the human's answer durably exists, so the decider
    /// reads "you asked X, the human answered Y" off the next turn's prior-decision outcome. Pure + deterministic.
    /// </summary>
    public static string FoldAnswer(string? question, string token, string? answer) =>
        JsonSerializer.Serialize(new { question, askHumanToken = token, answer }, AgentJson.Options);

    /// <summary>Read the human's recorded answer text from an ask_human outcome (null until the wait resolved + the answer was folded in). The decider sees "you asked X, the human answered Y" on the next turn.</summary>
    public static string? ReadAskHumanAnswer(string? outcomeJson) => ReadStringField(outcomeJson, "answer");

    /// <summary>Read the human's free-text answer (the <c>comment</c>) from a resolved Action wait's <c>{ action, by, comment }</c> payload. Empty string when absent/malformed (a click with no comment). The rehydrate fold AND the executor's resolved-wait recovery both read the answer through here.</summary>
    public static string ReadAnswerComment(string? payloadJson) => ReadStringField(payloadJson, "comment") ?? "";

    /// <summary>Best-effort read of a top-level string field from an outcome object (null when absent / malformed / not a string).</summary>
    private static string? ReadStringField(string? outcomeJson, string field)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return null;

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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

    /// <summary>
    /// Project ONE spawned agent's terminal facts into the compact, decider-visible <see cref="SupervisorAgentResult"/>
    /// (SOTA #2) — the SINGLE source of truth both the rehydrate fold (decider view) and the <c>merge</c> executor
    /// consume, so the two can never drift on which fields an agent exposes. <paramref name="statusName"/> is the
    /// authoritative AgentRun ROW status; <paramref name="rowError"/> is the ROW error (a cancelled/abandoned agent
    /// sets it with a NULL <paramref name="resultJson"/>), so the error surfaces even when the run wrote no result.
    /// Reads only bounded fields off the result — never the patch/transcript — so it needs no artifact-store fetch
    /// and stays a pure function of immutable post-terminal state (replay-deterministic).
    /// </summary>
    public static SupervisorAgentResult ProjectCompact(Guid agentRunId, string statusName, string? rowError, string? resultJson)
    {
        var result = string.IsNullOrWhiteSpace(resultJson) ? null : TryDeserializeResult(resultJson);

        return new SupervisorAgentResult
        {
            AgentRunId = agentRunId,
            Status = statusName,
            Summary = result?.Summary,
            Error = result?.Error ?? rowError,
            ChangedFiles = result?.ChangedFiles ?? Array.Empty<string>(),
            ProducedBranch = result?.ProducedBranch,
        };
    }

    /// <summary>Best-effort deserialize of a persisted <c>AgentRunResult</c> (null on malformed) — the compact projection tolerates a corrupt result the same way the merge path does.</summary>
    private static AgentRunResult? TryDeserializeResult(string resultJson)
    {
        try { return JsonSerializer.Deserialize<AgentRunResult>(resultJson, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Fold the spawned agents' COMPACT results into a spawn/retry decision's recorded outcome (SOTA #2), ADDITIVELY:
    /// the existing <c>agentRunIds</c> + <c>agentCount</c> are read OFF THE INPUT and re-emitted byte-intact (so the
    /// E5 counters that read <c>agentCount</c> are unperturbed), with an <c>agentResults</c> array appended. Returns
    /// the input UNCHANGED when it staged no agents (a zero-agent spawn keeps its <c>note</c> field — re-emitting a
    /// fixed shape would drop it + trigger a spurious write). Deterministic + idempotent: same terminal agents →
    /// same bytes, so the rehydrate persist no-ops after the first post-barrier stamp.
    /// </summary>
    public static string FoldAgentResults(string? spawnOutcomeJson, IReadOnlyList<SupervisorAgentResult> agentResults)
    {
        var agentRunIds = ReadStagedAgentRunIds(spawnOutcomeJson);

        if (agentRunIds.Count == 0) return spawnOutcomeJson ?? "";

        var agentCount = ReadStagedAgentCount(spawnOutcomeJson);

        return JsonSerializer.Serialize(new { agentRunIds, agentCount, agentResults }, AgentJson.Options);
    }

    /// <summary>Read the folded compact agent results from a spawn/retry outcome (empty when absent/malformed/not-yet-folded). The decider sees these via the rendered outcome; a merge / scorecard can also read them.</summary>
    public static IReadOnlyList<SupervisorAgentResult> ReadAgentResults(string? outcomeJson)
    {
        if (string.IsNullOrWhiteSpace(outcomeJson)) return Array.Empty<SupervisorAgentResult>();

        try
        {
            var root = JsonDocument.Parse(outcomeJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("agentResults", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<SupervisorAgentResult>();

            return arr.Deserialize<List<SupervisorAgentResult>>(AgentJson.Options) ?? (IReadOnlyList<SupervisorAgentResult>)Array.Empty<SupervisorAgentResult>();
        }
        catch (JsonException)
        {
            return Array.Empty<SupervisorAgentResult>();
        }
    }
}
