using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Decisions;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The decision-ARBITER drain (Decision substrate D4c-2): the supervisor's per-turn side-channel that helps unblock its
/// CHILD agent runs. For each pending child decision the arbiter brain (fail-closed-to-escalate) decides answer-or-escalate;
/// an ANSWER is actuated against the child grain via the supervisor-author answer path (which RE-RUNS the fail-closed
/// floor), an ESCALATE — or a floor-forced RequiresHuman, the floor having the last word over a wrong arbiter — is LEFT
/// in the cross-grain "Needs decision" queue (D3) for a human. That queue is the ONE generic human surface, so the
/// supervisor invents no escalation park of its own (a human answering a separate supervisor card would not resolve the
/// child's own grain anyway).
///
/// <para>It is a PURE side-channel: it resolves CHILD-grain decisions, never the supervisor's own turn, so it always
/// returns to the delivery decider. Idempotent WITHOUT the turn's exactly-once ledger claim — each answer is a
/// resolve-once CAS on the child grain (a re-run hits AlreadyResolved), and it runs only on the LIVE decide path
/// (<c>ChooseDecisionAsync</c>, which the frozen in-flight replay bypasses), so the arbiter's non-determinism can never
/// strand a row. It writes NO node-keyed resume payload and parks on nothing, so the early-wake collision class is
/// structurally untouchable here.</para>
///
/// <para>AC3 (never silent): an ANSWER records its rationale DURABLY on the child grain (the answer's
/// <c>DecisionAnswer.Rationale</c>); an ESCALATE records the arbiter's rationale to the RUN LOG and leaves the decision
/// in the queue, where the human triages it with the agent's OWN question + blocking-reason ("why it was raised").
/// Surfacing the arbiter's escalation rationale ON the queue card is deferred decision-observability — not lost, just
/// not yet projected onto the human surface.</para>
///
/// <para>Reachability (no early-wake in this slice): the supervisor wakes when a child resolves a wait, so the drain
/// acts on a child blocked on a live decision only on a LATER normal turn (once its siblings resolve the wait-for-all
/// barrier), not the instant the child blocks. Interim correctness rests on the child decision's own timeout + re-issue
/// and the human-answerable D3 queue — never on the supervisor freeing it promptly. The event-driven early-wake that
/// closes the latency is the deferred D4d follow-up.</para>
/// </summary>
public sealed partial class SupervisorTurnService
{
    /// <summary>Drain every pending child decision once this turn — auto-answer the confident ones, leave the rest for a human. A no-op when the run has no blocked children (the common path). Bounded: the list is the run's blocked children (≤ its spawned agents).</summary>
    internal async Task ArbitratePendingChildDecisionsAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        foreach (var decision in context.PendingChildDecisions)
        {
            // Best-effort PER CHILD (matching the substrate's skip-a-bad-row resilience): the arbiter never throws except
            // on cancellation, but the answer write CAN hit an infra error — and one child's failure must not abort
            // arbitrating its siblings. A skipped child stays AwaitingApproval in the queue → re-arbitrated next turn
            // (idempotent via the child-grain CAS). Cancellation still propagates (the run is being torn down).
            try
            {
                await ArbitrateOneAsync(decision, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Supervisor arbiter drain skipped child decision {DecisionId} (agent run {AgentRunId}) on an unexpected error — left in the queue, retried next turn", decision.Id, decision.AgentRunId);
            }
        }
    }

    /// <summary>Arbitrate ONE child decision: escalate (leave it in the queue) or auto-answer it via the supervisor-author path. The arbiter NEVER throws except on cancellation — an escalate IS the safe steady state — so there is no try/catch.</summary>
    private async Task ArbitrateOneAsync(PendingDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var verdict = await _arbiter.DecideAsync(decision, context.TeamId, context.SupervisorModelId, context.Goal, cancellationToken).ConfigureAwait(false);

        if (!verdict.IsAnswer)
        {
            LogEscalated(decision, verdict.Rationale);

            return;
        }

        var result = await _decisionAnswer.AnswerAsSupervisorAsync(decision.Id, verdict.SelectedOptions, verdict.FreeText, verdict.Rationale, context.TeamId, cancellationToken).ConfigureAwait(false);

        LogAnswerOutcome(decision, result);
    }

    private void LogEscalated(PendingDecision decision, string rationale) =>
        _logger.LogInformation("Supervisor arbiter ESCALATED child decision {DecisionId} (agent run {AgentRunId}) to a human — left in the queue: {Rationale}", decision.Id, decision.AgentRunId, rationale);

    /// <summary>Log how an auto-answer landed: Answered = auto-resolved; anything else left the decision for a human — RequiresHuman = the floor overrode the arbiter (defense-in-depth); AlreadyResolved/NotFound = a human/deadline raced it (benign); Invalid = a mis-shaped arbiter answer.</summary>
    private void LogAnswerOutcome(PendingDecision decision, AnswerDecisionResult result)
    {
        if (result.Outcome == DecisionAnswerOutcome.Answered)
            _logger.LogInformation("Supervisor arbiter AUTO-ANSWERED child decision {DecisionId} (agent run {AgentRunId})", decision.Id, decision.AgentRunId);
        else
            _logger.LogInformation("Supervisor arbiter did not resolve child decision {DecisionId} (agent run {AgentRunId}) — {Outcome}; left for a human", decision.Id, decision.AgentRunId, result.Outcome);
    }
}
