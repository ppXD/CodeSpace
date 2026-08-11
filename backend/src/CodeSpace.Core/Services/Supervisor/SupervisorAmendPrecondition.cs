using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The HARD precondition an amend proposal must clear before its co-sign card is even posted (amend-acceptance arc,
/// B4 — the MAJOR-3 rung): the target subtask's LATEST server-recorded verdict must be an INFRA-classed failure —
/// the check itself could not run (<see cref="Agents.AgentAcceptanceContract.IsInfraFailure(string?, bool)"/>:
/// grader fault, environment, incomplete spec). A check that RAN and rejected the work is evidence against the
/// WORK, and letting the model amend it away is the "mark its own homework" channel this arc exists to close — the
/// server's own verdict decides eligibility, never the model's claim about it ("not SHOULD").
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
    public static string? Reject(SupervisorTurnContext context, SupervisorAmendAcceptancePayload amend)
    {
        var latest = SupervisorDependencyGate.LatestResultsBySubtask(context).GetValueOrDefault(amend.SubtaskId);

        if (latest is null)
            return $"subtask '{amend.SubtaskId}' has never been attempted — an oracle is only amendable against the evidence of a graded failure; spawn it first";

        if (SupervisorOutcome.IsWaived(latest))
            return $"subtask '{amend.SubtaskId}' is already WAIVED — there is no oracle left to amend";

        if (latest.AcceptancePassed == true)
            return $"subtask '{amend.SubtaskId}'s check PASSED on its latest attempt — there is nothing wrong with the oracle to amend";

        if (latest.AcceptancePassed is null)
            return $"subtask '{amend.SubtaskId}'s latest attempt was never graded — an oracle is only amendable against the evidence of a graded failure";

        if (!Agents.AgentAcceptanceContract.IsInfraFailure(latest.AcceptanceDetail, SupervisorOutcome.ResultShowsWork(latest)))
            return $"subtask '{amend.SubtaskId}'s check RAN and rejected the work ({latest.AcceptanceDetail}) — that is evidence against the WORK, not the check; fix the work or retry it. An oracle is only amendable when its failure is infra-classed (the check itself could not run)";

        return null;
    }

    /// <summary>Cap on the quoted evidence tail — the card shows the diagnosis headline, not the whole log.</summary>
    internal const int MaxQuotedTailChars = 400;

    /// <summary>The raw server verdict appended to the POSTED card body (MAJOR-3's third leg: the co-signer rules on the server's own evidence, never only the model's framing) — display-only; the tape payload and the parked question stay canonical.</summary>
    public static string? RawVerdictSuffix(SupervisorTurnContext context, string subtaskId)
    {
        var latest = SupervisorDependencyGate.LatestResultsBySubtask(context).GetValueOrDefault(subtaskId);

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
