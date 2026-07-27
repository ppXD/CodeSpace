using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The run-level facts the completion reducer needs, read off the supervisor tape. Pure — every field comes from the
/// durable decision bytes, so a replay reaches the identical facts.
///
/// <para>Extracted from <c>CompletionAssessmentComposer</c> so the DB-backed compose and the tape-only projection
/// (<see cref="SupervisorTapeCompletion"/>) share ONE reading rather than a copy. A second implementation of this is
/// how the live-model gates came to score a prompt production does not ship.</para>
/// </summary>
public static class SupervisorCompletionFacts
{
    /// <summary>Fold the tape's terminal facts under an assumed terminal <paramref name="status"/>. A run with no decisions at all counts as orderly (nothing has failed to close).</summary>
    public static CompletionRunFacts FromTape(WorkflowRunStatus status, IReadOnlyList<SupervisorPriorDecision> decisions)
    {
        var lastStop = decisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop && SupervisorDecisionStateMachine.IsTerminal(d.Status));
        var classification = lastStop is null ? null : SupervisorOutcome.ClassifyStop(lastStop.PayloadJson, lastStop.OutcomeJson);

        return new CompletionRunFacts
        {
            TerminalStatus = status,
            HadOrderlyTerminal = decisions.Count == 0 || lastStop is not null,
            ForcedStopReason = classification?.Kind == SupervisorStopKind.Forced ? classification.Reason ?? "forced stop" : null,
            SelfReportedGiveUp = classification?.Kind == SupervisorStopKind.GaveUp,
            SelfReportedAbstention = classification?.Kind == SupervisorStopKind.NeedsClarification,
        };
    }
}
