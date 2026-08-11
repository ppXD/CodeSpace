using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The ONE invariant <see cref="SupervisorDecisionSchema"/> cannot express: the chosen <c>kind</c>'s payload
/// sub-object must actually be PRESENT. The schema's top level requires only <c>kind</c> (a per-kind conditional
/// <c>required</c> needs <c>if/then</c>, which the forced-tool wire accepts but does not enforce — live-probed
/// 2026-08-07: the gateway 200s on <c>allOf</c>+<c>if/then</c> and still emits payload-less and
/// top-level-flattened spawns). Checked on the RAW bound decision BEFORE projection, because
/// <see cref="SupervisorDecisionProjector"/> substitutes an empty payload for a missing sub-object — past that
/// point the executor rejects a payload the model never wrote, the rendered correction quotes that substitute,
/// and the model re-authors the same defective shape (live-observed: 140 rejected spawns in one eval run).
///
/// <para>Kinds whose schema sub-object declares no required field (<c>merge</c> — an empty merge is a legitimate
/// "merge everything mergeable") or carries no sub-object at all (<c>resolve</c>) are exempt. A unit
/// drift-detector derives the demanded set from <see cref="SupervisorDecisionSchema.ResponseSchema"/> itself so
/// this class and the schema cannot disagree silently. Deep semantic validation stays where it lives today:
/// unbindable shapes go to the bind repair, plan-graph errors to <see cref="SupervisorPlanValidator"/>,
/// everything else to the executor.</para>
/// </summary>
internal static class SupervisorDecisionCoherence
{
    /// <summary>The named defect a repair prompt can quote, or null when the decision carries the payload its kind names. The spawn/retry emptiness arms mirror the executor's own rejection predicates — at this point the dependency clamp has not run, so an empty spawn is always the model's own authorship, never a server-emptied fan-out.</summary>
    public static string? MissingPayload(SupervisorModelDecision model) => model.Kind switch
    {
        SupervisorDecisionKinds.Plan when model.Plan is null => Missing(model.Kind, "plan", "'goal' and 'subtasks'"),
        SupervisorDecisionKinds.Spawn when model.Spawn is null => Missing(model.Kind, "spawn", "a non-empty 'subtaskIds' array"),
        SupervisorDecisionKinds.Spawn when model.Spawn is { SubtaskIds.Count: 0 } => "the 'spawn' object's 'subtaskIds' array is EMPTY — a spawn must name at least one plan-declared subtask id inside 'spawn.subtaskIds'",
        SupervisorDecisionKinds.Retry when model.Retry is null => Missing(model.Kind, "retry", "a 'subtaskId'"),
        SupervisorDecisionKinds.Retry when string.IsNullOrWhiteSpace(model.Retry!.SubtaskId) => "the 'retry' object's 'subtaskId' is BLANK — a retry must name the one plan-declared subtask id to re-run inside 'retry.subtaskId'",
        SupervisorDecisionKinds.AskHuman when model.AskHuman is null => Missing(model.Kind, "askHuman", "a 'question'"),
        SupervisorDecisionKinds.AskHuman when string.IsNullOrWhiteSpace(model.AskHuman!.Question) => "the 'askHuman' object's 'question' is BLANK — ask_human must carry the question a human should answer inside 'askHuman.question'",
        SupervisorDecisionKinds.Stop when model.Stop is null => Missing(model.Kind, "stop", "'outcome' and 'summary'"),
        SupervisorDecisionKinds.AmendAcceptance when model.AmendAcceptance is null => Missing(model.Kind, "amendAcceptance", "a 'subtaskId', a 'reason', and either 'waive: true' or a replacement 'acceptance'"),
        SupervisorDecisionKinds.AmendAcceptance when string.IsNullOrWhiteSpace(model.AmendAcceptance!.SubtaskId) => "the 'amendAcceptance' object's 'subtaskId' is BLANK — an amendment must name the one plan-declared subtask whose check it targets inside 'amendAcceptance.subtaskId'",
        SupervisorDecisionKinds.AmendAcceptance when string.IsNullOrWhiteSpace(model.AmendAcceptance!.Reason) => "the 'amendAcceptance' object's 'reason' is BLANK — an amendment must carry the evidence that the current check is wrong inside 'amendAcceptance.reason' (it is quoted onto the human approval card)",
        SupervisorDecisionKinds.AmendAcceptance when !model.AmendAcceptance!.Waive && model.AmendAcceptance!.Acceptance is null => "the 'amendAcceptance' object proposes neither a replacement 'acceptance' nor 'waive: true' — an amendment must either carry the replacement check or explicitly waive verification",
        _ => null,
    };

    private static string Missing(string kind, string property, string fields) =>
        $"the decision chose kind '{kind}' but carries NO '{property}' object — its payload is only read from INSIDE a '{property}' object carrying {fields}; fields written anywhere else (e.g. at the top level of the decision) are never read";
}
