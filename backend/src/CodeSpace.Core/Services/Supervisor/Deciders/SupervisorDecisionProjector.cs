using System.Text.Json;
using System.Text.Json.Nodes;
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
///
/// <para>The DECISION-LEVEL <see cref="SupervisorModelDecision.Rationale"/> (why + evidence) is injected at the ROOT
/// of the canonical payload for EVERY verb uniformly — so the trace explains WHY, read back generically via
/// <c>SupervisorOutcome.ReadRationale</c>. A decision that authored NO rationale serializes BYTE-IDENTICALLY to a
/// plain payload serialize (the merge path is skipped), so the idempotency-key bytes are unchanged and every
/// pre-rationale decision replays exactly as before.</para>
/// </summary>
public static class SupervisorDecisionProjector
{
    public static SupervisorDecision Project(SupervisorModelDecision model) => model.Kind switch
    {
        SupervisorDecisionKinds.Plan => Canonical(SupervisorDecisionKinds.Plan, model.Plan ?? new SupervisorPlanPayload(), model.Rationale),
        SupervisorDecisionKinds.Spawn => Canonical(SupervisorDecisionKinds.Spawn, model.Spawn ?? new SupervisorSpawnPayload(), model.Rationale),
        SupervisorDecisionKinds.Retry => Canonical(SupervisorDecisionKinds.Retry, model.Retry ?? new SupervisorRetryPayload { SubtaskId = "" }, model.Rationale),
        SupervisorDecisionKinds.AskHuman => Canonical(SupervisorDecisionKinds.AskHuman, model.AskHuman ?? new SupervisorAskHumanPayload { Question = "" }, model.Rationale),
        SupervisorDecisionKinds.Merge => Canonical(SupervisorDecisionKinds.Merge, model.Merge ?? new SupervisorMergePayload(), model.Rationale),
        SupervisorDecisionKinds.Resolve => Canonical(SupervisorDecisionKinds.Resolve, model.Resolve ?? new SupervisorResolvePayload(), model.Rationale),
        SupervisorDecisionKinds.Stop => Canonical(SupervisorDecisionKinds.Stop, model.Stop ?? new SupervisorStopPayload { Outcome = "completed" }, model.Rationale),
        _ => Canonical(SupervisorDecisionKinds.Stop, new SupervisorStopPayload { Outcome = "unknown-decision", Summary = $"The decider emitted an unrecognized kind '{model.Kind}'." }, model.Rationale),
    };

    private static SupervisorDecision Canonical<TPayload>(string kind, TPayload payload, SupervisorRationale? rationale) => new()
    {
        Kind = kind,
        PayloadJson = SerializeWithRationale(payload, rationale),
    };

    /// <summary>
    /// Serialize the verb payload, injecting the decision-level rationale at the payload's ROOT (alongside the verb's
    /// own fields) when the model authored one. A null / empty rationale takes the plain-serialize fast path —
    /// BYTE-IDENTICAL to the pre-rationale projector, so the idempotency key of a rationale-less decision never shifts.
    /// The merge path is deterministic (the rationale is appended last), so replay of a rationale-bearing decision is stable.
    /// </summary>
    private static string SerializeWithRationale<TPayload>(TPayload payload, SupervisorRationale? rationale)
    {
        var payloadJson = JsonSerializer.Serialize(payload, AgentJson.Options);

        if (rationale is null || (rationale.Why is null && rationale.Evidence is null)) return payloadJson;

        var root = JsonNode.Parse(payloadJson)!.AsObject();
        root["rationale"] = JsonSerializer.SerializeToNode(rationale, AgentJson.Options);

        return root.ToJsonString(AgentJson.Options);
    }
}
