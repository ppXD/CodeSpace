using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>What one pass at the run's newest parked card did — the three outcomes the responder's loop branches on.</summary>
public enum ParkedAskDisposition
{
    /// <summary>Nothing is parked awaiting an answer, so there is nothing left to do.</summary>
    NothingParked,

    /// <summary>The card was answered and the run resumed.</summary>
    Answered,

    /// <summary>The card is one an unattended responder must NEVER answer — it stays parked for a real human, and the loop stops (the answer surface only ever targets the NEWEST card, so nothing behind it is reachable).</summary>
    LeftForAHuman,
}

/// <summary>
/// The unattended operator: the bounded answer loop a live-model gate runs so a brain that ASKS is not scored as a
/// brain that FAILED. A parked ask suspends the run until someone answers; with nobody there each ask self-advances
/// with a null answer, every one of them increments the no-progress streak, and the ninth trips
/// <see cref="SupervisorLane.DefaultMaxNoProgressDecisions"/> into a forced stop — so the acceptance floor never runs
/// and the gate ends up measuring the absence of a human instead of the model's completion.
///
/// <para>The loop is the whole policy, kept behind two delegates so it is pinned by a unit test without a live model, a
/// database, or a run: <c>answerOne</c> disposes of the newest parked card, <c>drain</c> rides the resume's
/// re-dispatch to settlement. The caller supplies the real production service for the first and its own drive helper
/// for the second.</para>
/// </summary>
public static class UnattendedAskResponder
{
    /// <summary>
    /// The scripted answer given to every parked card. It leads with the production approval word so it satisfies EVERY
    /// approval-shaped card — a plan confirmation, a gate escalation, an irreversible-verb approval — each of which
    /// reads exactly this prefix; a plain content ask is satisfied by any answer at all. Derived from the constant
    /// rather than spelled out, so a rename breaks the gates loudly instead of silently downgrading every answer into a
    /// rejection. The one card family it must NOT reach is the amend co-sign — see <see cref="MustLeaveForAHuman"/>.
    /// </summary>
    public static readonly string ApprovalAnswer = $"{SupervisorApprovalRequest.ApproveReply} — proceed";

    /// <summary>How many cards one attempt will answer. Comfortably above the no-progress bound an unanswered arc dies on (8) and well under the 30-decision budget, so a converging run is never starved while a brain that only ever asks still terminates as the miss it is.</summary>
    public const int MaxAnsweredAsks = 12;

    /// <summary>
    /// Whether a parked card must be LEFT for a real human rather than answered by the script. An
    /// <c>amend_acceptance</c> co-sign card is an <c>ask_human</c> like any other, and
    /// <see cref="SupervisorAmendAcceptance.IsApprovedAmendCard"/> approves on exactly the <c>approve</c> prefix
    /// <see cref="ApprovalAnswer"/> carries — so a blanket responder would co-sign the brain's own proposal to rewrite
    /// or waive a subtask's acceptance oracle, and the run would then earn a PASSED grade against the check it just
    /// talked its way out of. That is precisely what the co-sign chain exists to prevent: a run must never mark its own
    /// homework. Keyed on the marker ALONE (not <see cref="SupervisorAmendAcceptance.IsAmendCard"/>, which also needs a
    /// parseable proposal) so a malformed amend card is refused too — the safe direction.
    /// </summary>
    public static bool MustLeaveForAHuman(string? askPayloadJson) => SupervisorAmendAcceptance.QuestionCarriesMarker(askPayloadJson);

    /// <summary>Answer parked cards until none is parked, one must be left for a human, or the bound is reached. Returns how many were answered and how many were left parked — both ride the verdict line, so an attempt that cost nine answers, or that stopped at an oracle amendment, stays legible even when it passes.</summary>
    public static async Task<(int Answered, int LeftForAHuman)> AnswerAllAsync(Func<string, Task<ParkedAskDisposition>> answerOne, Func<Task> drain)
    {
        var answered = 0;
        var leftForAHuman = 0;

        while (answered < MaxAnsweredAsks)
        {
            var disposition = await answerOne(ApprovalAnswer);

            if (disposition == ParkedAskDisposition.LeftForAHuman) { leftForAHuman++; break; }

            if (disposition != ParkedAskDisposition.Answered) break;

            answered++;
            await drain();
        }

        return (answered, leftForAHuman);
    }
}
