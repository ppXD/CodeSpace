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
/// <para>An object that is PRESENT but EMPTY is the same defect wearing braces — a <c>plan</c> declaring no subtask
/// and a <c>spawn</c> naming no id are both unexecutable authorship, and neither is visible to the validators
/// downstream (<see cref="SupervisorPlanValidator"/> checks <c>dependsOn</c> EDGES, of which a subtask-less plan has
/// none), so both are named here.</para>
///
/// <para>Kinds whose schema sub-object declares no required field (<c>merge</c> — an empty merge is a legitimate
/// "merge everything mergeable") or carries no sub-object at all (<c>resolve</c>) are exempt. A unit
/// drift-detector derives the demanded set from <see cref="SupervisorDecisionSchema.ResponseSchema"/> itself so
/// this class and the schema cannot disagree silently. Deep semantic validation stays where it lives today:
/// unbindable shapes go to the bind repair, plan-graph errors to <see cref="SupervisorPlanValidator"/>,
/// everything else to the executor.</para>
///
/// <para><see cref="MisdirectedRetry"/> is the sibling invariant no schema could ever express, because it is about
/// the RUN and not the reply: a retry may not re-run a unit that is already done while other units are still failed.
/// It takes the tape for that reason, and it is the only check here that does.</para>
/// </summary>
internal static class SupervisorDecisionCoherence
{
    /// <summary>The named defect a repair prompt can quote, or null when the decision carries the payload its kind names. The spawn/retry emptiness arms mirror the executor's own rejection predicates — at this point the dependency clamp has not run, so an empty spawn is always the model's own authorship, never a server-emptied fan-out.</summary>
    public static string? MissingPayload(SupervisorModelDecision model) => model.Kind switch
    {
        SupervisorDecisionKinds.Plan when model.Plan is null => Missing(model.Kind, "plan", "'goal' and 'subtasks'"),
        SupervisorDecisionKinds.Plan when model.Plan is { Subtasks.Count: 0 } => "the 'plan' object's 'subtasks' array is EMPTY — a plan must declare at least one subtask inside 'plan.subtasks' (a subtask-less plan can never be spawned, retried or graded)",
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

    /// <summary>
    /// The retry's TARGET invariant — the sibling of <see cref="MissingPayload"/> that needs the run's own facts, so
    /// it takes the tape rather than the decision alone: a <c>retry</c> may not re-run a unit that is ALREADY done
    /// while other units are still failed. Live shape (golden <c>five-subtask-middle-failed</c>, two consecutive main
    /// runs 33945398336 + 33946934743): the brain answered a fan-out with four succeeded units and one failed one by
    /// retrying <c>s1</c> — a succeeded, accepted unit — spending the turn on work that was finished and leaving the
    /// actual failure untouched. A blank target is not this defect: <see cref="MissingPayload"/> already owns it.
    ///
    /// <para>Deliberately NARROW, because a false correction costs a round-trip on a decision the model got right:
    /// the target must be Succeeded AND still accepted (a rejected, waived, or amendment-stale unit is a LEGITIMATE
    /// retry target and reads null here), and at least one other unit must be genuinely <c>Failed</c> — an
    /// acceptance-rejected unit does not arm the rule, and neither does the P4-1 under-claim (a unit that reported
    /// failure while its own check passed is objectively done, and the recitation already says not to retry it).</para>
    ///
    /// <para>It ASKS and never re-aims: the correction quotes the failed unit ids and the model's own reply, and
    /// whatever comes back is the decision — a reply that re-emits the SAME target is the model's answer on the
    /// evidence, not a defect to correct twice.</para>
    /// </summary>
    public static string? MisdirectedRetry(SupervisorModelDecision model, IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        if (model.Kind != SupervisorDecisionKinds.Retry) return null;

        var target = model.Retry?.SubtaskId;

        if (string.IsNullOrWhiteSpace(target) || !IsFinishedAndAccepted(target, priorDecisions)) return null;

        var failed = SupervisorRecitation.LatestPlanSubtasks(priorDecisions).Select(s => s.Id).Where(id => IsFailed(id, priorDecisions)).ToList();

        if (failed.Count == 0) return null;

        return $"the 'retry' targets '{target}', whose latest attempt SUCCEEDED and is still accepted — re-running it cannot advance the run, "
             + $"while {string.Join(", ", failed)} {(failed.Count == 1 ? "is" : "are")} still FAILED and unretried";
    }

    /// <summary>A unit whose freshest attempt succeeded and is still accepted — nothing left to re-run. A REJECTED, WAIVED, or amendment-stale unit is excluded: each is a legitimate retry target the recitation itself points the model at.</summary>
    private static bool IsFinishedAndAccepted(string subtaskId, IReadOnlyList<SupervisorPriorDecision> priors)
    {
        if (SupervisorAmendObligation.IsOutstanding(priors, subtaskId)) return false;

        var (_, result) = SupervisorRecitation.LatestAttemptFor(subtaskId, priors);

        return result is { Status: "Succeeded" } && result.AcceptancePassed != false && !SupervisorOutcome.IsWaived(result);
    }

    /// <summary>A unit whose freshest attempt genuinely failed — the work a retry is owed. Excludes the P4-1 under-claim (reported failed, own check PASSED): that unit is objectively done.</summary>
    private static bool IsFailed(string subtaskId, IReadOnlyList<SupervisorPriorDecision> priors)
    {
        var (_, result) = SupervisorRecitation.LatestAttemptFor(subtaskId, priors);

        return result is { Status: "Failed" } && result.AcceptancePassed != true;
    }

    private static string Missing(string kind, string property, string fields) =>
        $"the decision chose kind '{kind}' but carries NO '{property}' object — its payload is only read from INSIDE a '{property}' object carrying {fields}; fields written anywhere else (e.g. at the top level of the decision) are never read";
}
