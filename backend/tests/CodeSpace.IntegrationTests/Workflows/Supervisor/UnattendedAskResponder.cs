using CodeSpace.Core.Services.Supervisor;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// The unattended operator: the bounded answer loop a live-model gate runs so a brain that ASKS is not scored as a
/// brain that FAILED. A parked ask suspends the run until someone answers; with nobody there each ask self-advances
/// with a null answer, every one of them increments the no-progress streak, and the ninth trips
/// <see cref="SupervisorLane.DefaultMaxNoProgressDecisions"/> into a forced stop — so the acceptance floor never runs
/// and the gate ends up measuring the absence of a human instead of the model's completion.
///
/// <para>The loop is the whole policy, kept behind two delegates so it is pinned by a unit test without a live model, a
/// database, or a run: <c>answerOne</c> answers the newest parked card (false ⇒ nothing is parked, which is how the loop
/// terminates), <c>drain</c> rides the resume's re-dispatch to settlement. The caller supplies the real production
/// service for the first and its own drive helper for the second.</para>
/// </summary>
public static class UnattendedAskResponder
{
    /// <summary>
    /// The scripted answer given to every parked card. It leads with the production approval word so it satisfies EVERY
    /// approval-shaped card — a plan confirmation, a gate escalation, an irreversible-verb approval, an amend co-sign —
    /// each of which reads exactly this prefix; a plain content ask is satisfied by any answer at all. Derived from the
    /// constant rather than spelled out, so a rename breaks the gates loudly instead of silently downgrading every
    /// answer into a rejection.
    /// </summary>
    public static readonly string ApprovalAnswer = $"{SupervisorApprovalRequest.ApproveReply} — proceed";

    /// <summary>How many cards one attempt will answer. Comfortably above the no-progress bound an unanswered arc dies on (8) and well under the 30-decision budget, so a converging run is never starved while a brain that only ever asks still terminates as the miss it is.</summary>
    public const int MaxAnsweredAsks = 12;

    /// <summary>Answer parked cards until none is parked or the bound is reached; returns how many were answered (the number the verdict line records, so an attempt that cost nine answers stays legible even when it passes).</summary>
    public static async Task<int> AnswerAllAsync(Func<string, Task<bool>> answerOne, Func<Task> drain)
    {
        var answered = 0;

        while (answered < MaxAnsweredAsks && await answerOne(ApprovalAnswer))
        {
            answered++;
            await drain();
        }

        return answered;
    }
}
