using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The raw shape a structured-LLM decider returns (PR-E E3) — the deserialized view of a
/// <see cref="SupervisorDecisionSchema.ResponseSchema"/>-valid object. The model picks ONE <see cref="Kind"/>
/// and fills that verb's payload sub-object; the decider then projects this into the canonical
/// <see cref="SupervisorDecision"/> (verb + canonical payload JSON) the turn loop hashes + records. A data
/// noun bound to the decider concern (it never leaves the decider), so it lives in Core beside the schema.
/// </summary>
public sealed record SupervisorModelDecision
{
    public string Kind { get; init; } = "";

    /// <summary>
    /// The model-authored DECISION-LEVEL rationale (why + evidence) — a decision annotation, not verb payload data,
    /// so it sits at the ROOT for EVERY verb uniformly (a plan, a spawn, a retry, a merge, a stop…), never nested in
    /// one verb's sub-object. The projector injects it into the canonical payload's root; null → the model gave none.
    /// </summary>
    public SupervisorRationale? Rationale { get; init; }

    public SupervisorPlanPayload? Plan { get; init; }

    public SupervisorSpawnPayload? Spawn { get; init; }

    public SupervisorRetryPayload? Retry { get; init; }

    public SupervisorAskHumanPayload? AskHuman { get; init; }

    public SupervisorMergePayload? Merge { get; init; }

    public SupervisorResolvePayload? Resolve { get; init; }

    public SupervisorStopPayload? Stop { get; init; }
}
