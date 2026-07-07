using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each supervisor ASK_HUMAN step with the operator's ANSWER — the decision the human made (approve, or the
/// change they asked for). Read straight off the decision's outcome (<see cref="SupervisorOutcome.ReadAskHumanAnswer"/>)
/// and keyed by the decision's timeline event id (<see cref="SupervisorDecisionTimelineMap.EventId"/>), so the walk hangs
/// it on the SAME "asked you" step the supervisor describer produced. Carrying the answer as its OWN field (not folded
/// into the question prose) lets the frontend render the operator's decision as a distinct line, unambiguously — a string
/// split of the joined "{question} — {answer}" summary can't recover it when the question itself contains an em-dash.
/// A still-pending ask (not yet answered) contributes nothing; a run with no supervisor tape contributes nothing.
///
/// <para>Also flags a REVIEW-GATE ESCALATION ask (the hard-Gate ladder exhausted and parked the run on the human) via
/// the pinned payload marker (<see cref="SupervisorGateEscalation.QuestionCarriesMarker"/>) — set even while the ask is
/// still PENDING, so the "review-blocked" framing shows the moment the run parks, not only after the answer lands.
/// A PLAN-CONFIRMATION card (<see cref="SupervisorPlanConfirmation.QuestionCarriesMarker"/>) is flagged the same way,
/// so the frontend suppresses the generic answer bar on it — the plan checklist card is that park's richer answer
/// surface (structured approve / request-changes), and two bars answering one wait would just be noise.</para>
/// </summary>
public sealed class AskAnswerFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;

    public AskAnswerFactsSource(ISupervisorDecisionLog decisions)
    {
        _decisions = decisions;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var decision in tape.Where(d => d.DecisionKind == SupervisorDecisionKinds.AskHuman))
        {
            var answer = SupervisorOutcome.ReadAskHumanAnswer(decision.OutcomeJson);
            var escalation = SupervisorGateEscalation.QuestionCarriesMarker(decision.PayloadJson);
            var planGate = SupervisorPlanConfirmation.QuestionCarriesMarker(decision.PayloadJson);

            if (!string.IsNullOrWhiteSpace(answer) || escalation || planGate)
                facts[SupervisorDecisionTimelineMap.EventId(decision)] = new JournalStepFacts { Answer = string.IsNullOrWhiteSpace(answer) ? null : answer.Trim(), ReviewEscalation = escalation, PlanConfirmation = planGate };
        }

        return facts;
    }
}
