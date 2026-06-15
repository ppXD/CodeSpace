using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The per-verb canonical payloads a supervisor decision freezes into its ledger row (PR-E E3 — data nouns,
/// Rule 18.1). Each is the deterministic JSON the decider emits + the turn loop hashes into the server-derived
/// idempotency key, so the SAME decision at the SAME turn produces the SAME bytes → the same key → exactly-once
/// on replay. The decider canonicalizes via <c>AgentJson.Options</c>; these records are the deserialized view
/// the executor reads to drive the side effect.
///
/// <para>These are server-side projections of the model's <c>SupervisorModelDecision</c> — the model never
/// addresses graph topology (no node id / type key / run id); the server turns a verb + bounded payload into a
/// side effect.</para>
/// </summary>
public sealed record SupervisorPlanPayload
{
    public string Goal { get; init; } = "";

    public IReadOnlyList<SupervisorPlannedSubtask> Subtasks { get; init; } = Array.Empty<SupervisorPlannedSubtask>();
}

/// <summary>One planned subtask the supervisor can later spawn / retry by <see cref="Id"/>.</summary>
public sealed record SupervisorPlannedSubtask
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Instruction { get; init; }
}

/// <summary>The <c>spawn</c> payload: prior-plan subtask ids to fan out as parallel agent runs (K = the list length).</summary>
public sealed record SupervisorSpawnPayload
{
    public IReadOnlyList<string> SubtaskIds { get; init; } = Array.Empty<string>();
}

/// <summary>The <c>retry</c> payload: ONE prior subtask id re-run as a fresh agent attempt, optionally with a revised instruction.</summary>
public sealed record SupervisorRetryPayload
{
    public required string SubtaskId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevisedInstruction { get; init; }
}

/// <summary>The <c>merge</c> payload: prior subtask ids whose recorded agent results to synthesize (empty = all), plus an optional synthesis instruction.</summary>
public sealed record SupervisorMergePayload
{
    public IReadOnlyList<string> SubtaskIds { get; init; } = Array.Empty<string>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SynthesisInstruction { get; init; }
}

/// <summary>The <c>ask_human</c> payload: the question to ask (parked for E4).</summary>
public sealed record SupervisorAskHumanPayload
{
    public required string Question { get; init; }
}

/// <summary>The <c>stop</c> payload: the terminal outcome label + a short summary.</summary>
public sealed record SupervisorStopPayload
{
    public required string Outcome { get; init; }

    public string Summary { get; init; } = "";
}
