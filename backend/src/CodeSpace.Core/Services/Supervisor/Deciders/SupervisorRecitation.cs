using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The plan RECITATION block (triad S8, the Manus lesson): a compact restatement of the CURRENT plan with each
/// item's LIVE state, re-derived from the tape every turn and injected at the PROMPT TAIL — the recency-biased
/// position — so a long-running supervisor never loses the plan under a growing prior-decision log. Pure over
/// <see cref="SupervisorPriorDecision"/>s (the same durable rows every other read derives from — never a second
/// source of truth): the newest plan's subtasks, each joined to its LATEST covering spawn/retry attempt's folded
/// result (positional subtaskIds[i] ↔ results[i], the platform's standard join). Null when no plan exists — a
/// planless run's prompt stays byte-identical.
/// </summary>
public static class SupervisorRecitation
{
    /// <summary>The block's pinned header — the recitation is a stable prompt landmark (tests + the model key on it).</summary>
    public const string Header = "CURRENT PLAN STATE (recite before deciding — unfinished items are the remaining work):";

    /// <summary>The stable, matchable prefix of an under-claim's recited state (P4-1) — <see cref="Render"/>'s unfinished-list gate matches on this rather than the full dynamic string (which carries the grader's detail).</summary>
    private const string UnderClaimPrefix = "reported failed, but its OWN acceptance check actually PASSED";

    public static string? Render(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var subtasks = LatestPlanSubtasks(priorDecisions);

        if (subtasks.Count == 0) return null;

        var builder = new StringBuilder(Header);
        var unfinished = new List<string>();

        foreach (var subtask in subtasks)
        {
            var state = StateFor(subtask.Id, priorDecisions);

            builder.AppendLine().Append($"- [{subtask.Id}] {subtask.Title}: {state}{EscalationNoteFor(subtask.Id, priorDecisions)}");

            // Authoring LINT, recited every turn until fixed (the free tier-0 check the supervisor lane's plans
            // never got): a half-authored acceptance spec (judge without rubric, schema check without schema) can
            // NEVER pass at grade time — telling the model NOW turns a paid clone + a fail-closed verdict + a retry
            // temptation into one re-plan. Pure over the authored spec; a valid/absent spec adds nothing.
            if (subtask.Acceptance is { } spec && Agents.AgentAcceptanceContract.ValidateAuthored(spec) is { } specError)
                builder.Append($" ⚠ its acceptance spec is INVALID as authored ({specError}) — it can never pass; re-plan this item's check.");

            // An under-claim (P4-1) reads its own guidance line ("do not retry, merge it") — it is objectively DONE,
            // just self-reported wrong, so it must not also land on the unfinished list and contradict its own text.
            if (state is not ("done (accepted)" or "done") && !state.StartsWith(UnderClaimPrefix, StringComparison.Ordinal))
                unfinished.Add(subtask.Id);
        }

        builder.AppendLine().Append(unfinished.Count == 0
            ? "Every plan item is finished — merge the results and drive to a verified stop."
            : $"Unfinished: {string.Join(", ", unfinished)}.");

        return builder.ToString();
    }

    /// <summary>The newest plan decision's subtasks — a re-plan supersedes (the same newest-plan rule the acceptance fold uses).</summary>
    private static IReadOnlyList<SupervisorPlannedSubtask> LatestPlanSubtasks(IReadOnlyList<SupervisorPriorDecision> priors)
    {
        for (var i = priors.Count - 1; i >= 0; i--)
            if (priors[i].DecisionKind == SupervisorDecisionKinds.Plan)
                return SupervisorOutcome.ReadPlanSubtasks(priors[i].PayloadJson);

        return Array.Empty<SupervisorPlannedSubtask>();
    }

    /// <summary>
    /// One subtask's live state off its LATEST covering spawn/retry: the folded result's status + acceptance verdict
    /// (accepted / REJECTED-with-detail / failed-with-error / the raw non-terminal status), a staged-but-unfolded
    /// attempt reads "running", and an un-staged subtask "pending". Newest-first scan, so a retry supersedes the
    /// original spawn — exactly the freshest-attempt rule the decider prompt already marks.
    /// </summary>
    internal static string StateFor(string subtaskId, IReadOnlyList<SupervisorPriorDecision> priors)
    {
        if (FindCoveringDecision(subtaskId, priors) is not { } decision) return "pending";

        var index = IndexOf(UnitSubtaskIds(decision), subtaskId);
        var results = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

        return index >= results.Count ? "running" : Describe(results[index]);   // staged, outcome not folded yet
    }

    /// <summary>
    /// A2 (P4-2) — the subtask's LATEST covering decision's OWN escalation note, purely a DISPLAY suffix (never
    /// folded into <see cref="StateFor"/>'s return value, which <see cref="Render"/>'s finished/unfinished gate
    /// pattern-matches exactly — concatenating it there would break that match for a "done"/"done (accepted)"
    /// escalated retry). A retry only ever stages ONE unit, so any escalation its covering decision recorded is
    /// unambiguously about THIS subtask. Empty when the covering decision recorded none (the common case).
    /// </summary>
    private static string EscalationNoteFor(string subtaskId, IReadOnlyList<SupervisorPriorDecision> priors) =>
        FindCoveringDecision(subtaskId, priors) is { } decision && SupervisorOutcome.ReadEscalation(decision.OutcomeJson) is { } escalation
            ? $" [escalated to {escalation.To}: {escalation.Reason}]"
            : "";

    /// <summary>The LATEST spawn/retry decision that staged this subtask id — the one shared walk <see cref="StateFor"/> and <see cref="EscalationNoteFor"/> both join off, so they can never disagree about WHICH attempt is the freshest one.</summary>
    private static SupervisorPriorDecision? FindCoveringDecision(string subtaskId, IReadOnlyList<SupervisorPriorDecision> priors)
    {
        for (var i = priors.Count - 1; i >= 0; i--)
        {
            var decision = priors[i];

            if (SupervisorDecisionKinds.StagesAgents(decision.DecisionKind) && IndexOf(UnitSubtaskIds(decision), subtaskId) >= 0)
                return decision;
        }

        return null;
    }

    /// <summary>
    /// Real-bug fix: a <c>retry</c> decision's payload carries the plan-local subtask id as a SINGULAR <c>subtaskId</c>
    /// field (<see cref="SupervisorRetryPayload.SubtaskId"/>), never the <c>spawn</c> payload's PLURAL <c>subtaskIds</c>
    /// array — so calling <see cref="SupervisorOutcome.ReadSpawnSubtaskIds"/> unconditionally (as this method did before
    /// this fix) always returned empty for a genuine retry, silently skipping it and leaving the recitation showing the
    /// STALE original-spawn state forever, contradicting <see cref="StateFor"/>'s own doc comment ("a retry supersedes
    /// the original spawn"). Mirrors the SAME kind-aware read <c>SupervisorTurnService.Rehydrate.UnitSubtaskIds</c>
    /// already uses for the identical join.
    /// </summary>
    private static IReadOnlyList<string> UnitSubtaskIds(SupervisorPriorDecision decision) =>
        decision.DecisionKind == SupervisorDecisionKinds.Spawn
            ? SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson)
            : SupervisorOutcome.ReadRetrySubtaskId(decision.PayloadJson) is { } id ? new[] { id } : Array.Empty<string>();

    private static string Describe(SupervisorAgentResult result) => result.Status switch
    {
        "Succeeded" when result.AcceptancePassed == true => "done (accepted)",
        // Same three-way split as the decider's verdict line — the recitation and the results section must never
        // give the weak brain CONTRADICTORY framings of the same row (one says REJECTED-retry, the other UNVERIFIED-replan).
        // Reads AcceptancePassed directly (not the newer Contradiction field) so a row folded BEFORE P4-1 shipped —
        // which carries AcceptancePassed but no Contradiction — keeps rendering identically; back-compat, not a shortcut.
        "Succeeded" when result.AcceptancePassed == false && IsInfraRejection(result) => $"done but its check COULD NOT RUN ({Truncate(result.AcceptanceDetail)}) — re-plan the check, do not retry the agent",
        "Succeeded" when result.AcceptancePassed == false => $"done but REJECTED by its acceptance check ({Truncate(result.AcceptanceDetail)})",
        "Succeeded" => "done",
        // P4-1: the agent gave up on work that its OWN check actually passed — the inverse of the over-claim above,
        // and previously unhandled here (a "Failed" row fell straight to the bare error line regardless of the
        // verdict), silently discarding the one signal that says "do not retry, this is already fine." Same
        // back-compat stance: reads AcceptancePassed directly, so it applies to every row, old or new.
        "Failed" when result.AcceptancePassed == true => $"{UnderClaimPrefix} ({Truncate(result.AcceptanceDetail)}) — the work is objectively fine; do not retry, merge it",
        "Failed" when result.AcceptancePassed == false && IsInfraRejection(result) => $"done but its check COULD NOT RUN ({Truncate(result.AcceptanceDetail)}) — re-plan the check, do not retry the agent",
        "Failed" => $"failed ({Truncate(result.Error ?? result.AcceptanceDetail)})",
        var other => (other ?? "running").ToLowerInvariant(),
    };

    /// <summary>The shared infra classification over the compact — the SAME split (and the SAME work-present read) the decider's verdict line renders, so the two prompt sections can't disagree about a row.</summary>
    private static bool IsInfraRejection(SupervisorAgentResult result) =>
        Agents.AgentAcceptanceContract.IsInfraFailure(result.AcceptanceDetail, SupervisorOutcome.ResultShowsWork(result));

    private static int IndexOf(IReadOnlyList<string> ids, string subtaskId)
    {
        for (var i = 0; i < ids.Count; i++)
            if (string.Equals(ids[i], subtaskId, StringComparison.Ordinal)) return i;

        return -1;
    }

    private static string Truncate(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "no detail" : detail.Length <= 160 ? detail : detail[..160] + "…";
}
