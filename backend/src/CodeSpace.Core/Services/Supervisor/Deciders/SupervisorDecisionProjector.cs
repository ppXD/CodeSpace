using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// Projects a schema-valid <see cref="SupervisorModelDecision"/> (the model's raw output) into the canonical
/// <see cref="SupervisorDecision"/> the turn loop records (PR-E E3). Pure + deterministic — same model decision
/// → same verb + same canonical payload bytes (serialized via <c>AgentJson.Options</c>) → same idempotency key.
/// Co-located with the decider (Rule 18); unit-pinned per verb.
///
/// <para>The model's verb sub-object becomes that verb's canonical payload record (a data noun, Rule 18.1). A
/// missing sub-object for the chosen kind falls back to a SAFE empty payload (an empty plan / spawn / merge) so
/// a malformed model response degrades cleanly rather than throwing — the executor then no-ops or stops, never
/// crashes. An unknown kind projects to a terminal <c>stop</c> (fail-closed).</para>
/// </summary>
public static class SupervisorDecisionProjector
{
    public static SupervisorDecision Project(SupervisorModelDecision model) => model.Kind switch
    {
        SupervisorDecisionKinds.Plan => Canonical(SupervisorDecisionKinds.Plan, model.Plan ?? new SupervisorPlanPayload()),
        SupervisorDecisionKinds.Spawn => Canonical(SupervisorDecisionKinds.Spawn, model.Spawn ?? new SupervisorSpawnPayload()),
        SupervisorDecisionKinds.Retry => Canonical(SupervisorDecisionKinds.Retry, model.Retry ?? new SupervisorRetryPayload { SubtaskId = "" }),
        SupervisorDecisionKinds.AskHuman => Canonical(SupervisorDecisionKinds.AskHuman, model.AskHuman ?? new SupervisorAskHumanPayload { Question = "" }),
        SupervisorDecisionKinds.Merge => Canonical(SupervisorDecisionKinds.Merge, model.Merge ?? new SupervisorMergePayload()),
        SupervisorDecisionKinds.Resolve => Canonical(SupervisorDecisionKinds.Resolve, model.Resolve ?? new SupervisorResolvePayload()),
        SupervisorDecisionKinds.Stop => Canonical(SupervisorDecisionKinds.Stop, model.Stop ?? new SupervisorStopPayload { Outcome = "completed" }),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "unknown-decision", Summary = $"The decider emitted an unrecognized kind '{model.Kind}'." }),
    };

    private static SupervisorDecision Canonical<TPayload>(string kind, TPayload payload) => new()
    {
        Kind = kind,
        PayloadJson = JsonSerializer.Serialize(payload, AgentJson.Options),
    };
}
