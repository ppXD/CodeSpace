using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// A model-authored per-agent dispatch spec (the L3→L4 step, arc B) — a data noun (Rule 18.1). When the supervisor
/// model spawns, instead of fanning out homogeneous clones of one operator profile it MAY author an
/// <c>agents[]</c> entry per planned subtask, giving each agent a distinct role, repo subset, and execution-envelope
/// REQUEST. The model only proposes; the SERVER clamps (permissions derived not raw, repos a subset of the operator's
/// bound set with no access upgrade, harness/runner against the allow-list, team/credential server-resolved) — so the
/// model does the semantic division of labour and the server stays authoritative over safety. Every field but
/// <see cref="SubtaskId"/> is optional; an absent field inherits the run-level profile default (and an absent
/// <c>agents[]</c> is byte-identical to the plain <c>subtaskIds</c> fan-out).
///
/// <para>Carried only in arc B's first slice — the executor consumes it (clamped) when threading the spawn into the
/// shared task builder.</para>
/// </summary>
public sealed record SupervisorAgentDispatch
{
    /// <summary>The planned subtask this agent runs — the fan-out key (each <c>agents[]</c> entry overrides one planned subtask, so a plan precedes a per-agent spawn). REQUIRED.</summary>
    public required string SubtaskId { get; init; }

    /// <summary>The model's semantic role for this agent (e.g. "backend implementer", "security reviewer"). The executor (B3) folds it into the spawned agent's GOAL/instruction so the agent runs in-role — it shapes the prompt, never permissions. Null → no distinct role (the plain goal).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    /// <summary>An optional revised instruction for this agent's subtask (overrides the planned instruction). Null → use the planned subtask's instruction.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GoalOverride { get; init; }

    /// <summary>The primary repository this agent targets — MUST be one the operator profile already bound (clamped server-side). Null → the profile's primary repo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RepositoryId { get; init; }

    /// <summary>The agent's related-repo SUBSET — a RAW JSON array of <c>{repositoryId, alias?, access?}</c> (the same shape as the profile's related repos), parsed through the shared authoring path and clamped to a subset of the operator's bound repos with no access upgrade. Null → the profile's related repos. Raw <see cref="JsonElement"/> to dodge enum-deserialization, mirroring <c>SupervisorAgentProfile.RelatedRepositories</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? TargetRepos { get; init; }

    /// <summary>A harness REQUEST (e.g. "codex-cli") — granted only if the operator's allow-list permits it, else the profile/harness default. Null → the profile default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Harness { get; init; }

    /// <summary>A model REQUEST for this agent. Null → the profile's model.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    /// <summary>An autonomy REQUEST (a named tier) — clamped to the run profile's autonomy CEILING (never raised past it) before the server derives the permissions. Null → the profile's autonomy.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutonomyLevel { get; init; }

    /// <summary>
    /// The Agent persona this agent embodies — the @-mention SLUG of one of the team's personas (rendered in the
    /// capability catalog), e.g. <c>"security-reviewer"</c>. The server resolves it to that team's
    /// <c>AgentDefinitionId</c> (the persona's system prompt / model / tools then merge into the run, exactly as the
    /// run-level profile persona does). FAIL-CLOSED on an unknown / foreign / deleted slug (a clean terminal, like an
    /// out-of-pool model) — the brain only picks from the team's own personas the catalog lists. Null → the run-level
    /// profile persona (byte-identical to a homogeneous spawn).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentDefinition { get; init; }
}
