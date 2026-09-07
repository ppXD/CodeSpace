using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The HARD precondition an amend proposal must clear before its co-sign card is even posted (amend-acceptance arc,
/// B4 — the MAJOR-3 rung): the target subtask's LATEST server-recorded verdict must be an INFRA-classed failure —
/// the check itself could not run (<see cref="Agents.AgentAcceptanceContract.IsInfraFailure(string?, bool)"/>:
/// grader fault, environment, incomplete spec). A check that RAN and rejected the work is evidence against the
/// WORK, and letting the model amend it away is the "mark its own homework" channel this arc exists to close — the
/// server's own verdict decides eligibility, never the model's claim about it ("not SHOULD"). That verdict is read
/// across the WHOLE tape, not the active plan generation — see <see cref="GradedAttempts"/> for why a re-plan must
/// not be able to hide the attempt an amendment is warranted by.
///
/// <para>Rejected proposals return synchronously with a named reason (the <c>RejectedAskHumanOutcome</c> family) —
/// no card is posted, no human interruption is spent, and the next turn's decider reads exactly why. The rejection
/// is deliberately surface-independent: a dodge-work amend in a no-surface run must reject, not degrade.
/// Pure over the tape — a replay re-derives the identical verdict.</para>
///
/// <para>The model's LEGITIMATE escape for a wrong-but-runnable oracle (the check runs and fails because it was
/// authored against tooling the repo never had) stays open: such failures classify infra at the source — a
/// missing tool fails the grader's process start (<c>grade-error:</c>) or the spec fails authoring validation
/// (<c>no-rubric</c>/<c>no-schema</c>) — while an in-process test failure is an exit code, classified Genuine.</para>
/// </summary>
public static class SupervisorAmendPrecondition
{
    /// <summary>Why this proposal must not reach a human, or null when it may — the target's latest verdict is a genuinely infra-classed failure.</summary>
    public static string? Reject(SupervisorTurnContext context, SupervisorAmendAcceptancePayload amend) => Reject(context.PriorDecisions, amend.SubtaskId);

    /// <summary>Whether ANY unit on this tape would clear <see cref="Reject"/> — the turn roster's availability reader for <c>amend_acceptance</c>. Derived from the precondition itself, never a second reading of it: a menu that offers the verb where the server refuses it synchronously costs the turn for nothing, and one that withholds it where a broken oracle really is amendable strands the run on a check that cannot pass.</summary>
    public static bool AnyAmendableUnit(SupervisorTurnContext context) =>
        GradedAttempts(context.PriorDecisions).Keys.Any(subtaskId => Reject(context.PriorDecisions, subtaskId) is null);

    /// <summary>Whether an amend proposal for THIS unit would clear <see cref="Reject"/> — the PER-UNIT reading <see cref="SupervisorReplanStanding.ExitFor"/> needs, so a steer that sends a stranded unit at <c>amend_acceptance</c> names the verb only where the server admits it (and therefore only where <see cref="AnyAmendableUnit"/> puts it on the turn's menu — the same gate, so the two cannot offer and forbid it in one prompt). Priors-only for the same reason the obligation walk has such an overload: the prompt renderers resolve it without a turn context.</summary>
    public static bool IsAmendable(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string subtaskId) => Reject(priorDecisions, subtaskId) is null;

    /// <summary>
    /// The graded evidence this gate rules on: every subtask's LATEST folded attempt across the WHOLE tape, NOT the
    /// active <see cref="SupervisorPlanWindow"/> generation. An amendment's purpose is to re-anchor a repaired check
    /// to the NEWEST plan, so the attempt that evidenced the broken one is exactly what a re-plan must not hide —
    /// and reading it through the window split this gate against itself: the arms below refused the discarded-co-sign
    /// tape with "never been attempted" (the window closes over the spawn that produced the evidence) while
    /// <see cref="SupervisorAmendObligation.StandingFor"/>, the decider's <c>Discarded</c> steer and the turn roster
    /// all read the whole tape. Golden <c>amended-oracle-discarded-by-replan</c> is what that costs: the prompt
    /// steered at <c>amend_acceptance</c> one screen under a menu reporting it unavailable, and the model answered
    /// with a third verb (decision eval 34085079257, 24/25).
    ///
    /// <para>RESIDUAL, named rather than guarded here: a subtask the newest plan no longer declares is now amendable
    /// on its superseded attempt's evidence, and a co-sign for it can never be consumed by a retry. A plan-membership
    /// arm would be its own admission rule (and would have to exempt plan-less tapes), and it would re-open exactly
    /// the split above one case narrower — the decider already renders that unit's verdict line and steer off the
    /// same whole-tape join (<c>UnitSteerStandings</c>). It belongs in a change that can be graded on that.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, SupervisorAgentResult> GradedAttempts(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        SupervisorDependencyGate.LatestResultsBySubtask(priorDecisions);

    /// <summary>The subtask-id overload every arm below actually reads — <see cref="Reject(SupervisorTurnContext, SupervisorAmendAcceptancePayload)"/>'s only inputs are the tape and the target, so the roster and the re-plan steers can ask the same question without inventing a proposal to ask it with.</summary>
    private static string? Reject(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string subtaskId)
    {
        // B6 (the re-enactment arm's live finding): after an approved amendment, the target's LATEST verdict is
        // still the dead oracle's failure — which passes the infra check below and let a live brain re-amend the
        // same subtask five times without ever retrying. One signed repair at a time: consume it first.
        if (SupervisorAmendObligation.IsOutstanding(priorDecisions, subtaskId))
            return $"subtask '{subtaskId}' already carries an approved amendment awaiting its retry — RETRY the subtask to re-grade under the co-signed check; do not amend it again";

        var latest = GradedAttempts(priorDecisions).GetValueOrDefault(subtaskId);

        if (latest is null)
            return $"subtask '{subtaskId}' has never been attempted — an oracle is only amendable against the evidence of a graded failure; spawn it first";

        if (SupervisorOutcome.IsWaived(latest))
            return $"subtask '{subtaskId}' is already WAIVED — there is no oracle left to amend";

        if (latest.AcceptancePassed == true)
            return $"subtask '{subtaskId}'s check PASSED on its latest attempt — there is nothing wrong with the oracle to amend";

        if (latest.AcceptancePassed is null)
            return $"subtask '{subtaskId}'s latest attempt was never graded — an oracle is only amendable against the evidence of a graded failure";

        if (!Agents.AgentAcceptanceContract.IsInfraFailure(latest.AcceptanceDetail, SupervisorOutcome.ResultShowsWork(latest)))
            return $"subtask '{subtaskId}'s check RAN and rejected the work ({latest.AcceptanceDetail}) — that is evidence against the WORK, not the check; fix the work or retry it. An oracle is only amendable when its failure is infra-classed (the check itself could not run)";

        return null;
    }

    /// <summary>Cap on the quoted evidence tail — the card shows the diagnosis headline, not the whole log.</summary>
    internal const int MaxQuotedTailChars = 400;

    /// <summary>The raw server verdict appended to the POSTED card body (MAJOR-3's third leg: the co-signer rules on the server's own evidence, never only the model's framing) — display-only; the tape payload and the parked question stay canonical.</summary>
    public static string? RawVerdictSuffix(SupervisorTurnContext context, string subtaskId)
    {
        var latest = GradedAttempts(context.PriorDecisions).GetValueOrDefault(subtaskId);

        if (latest?.AcceptanceDetail is null) return null;

        var suffix = new StringBuilder($"\n\nLatest server verdict for '{subtaskId}': {latest.AcceptanceDetail}");

        if (!string.IsNullOrEmpty(latest.AcceptanceEvidenceTail))
        {
            var tail = latest.AcceptanceEvidenceTail!;
            suffix.Append('\n').Append(tail.Length <= MaxQuotedTailChars ? tail : tail[..MaxQuotedTailChars] + "…");
        }

        return suffix.ToString();
    }
}
