using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The scoped turn-loop service (Rule 16 — owns the ledger + decider + executor so the node stays thin;
/// Rule 18.2 — its own concern under <c>Services/Supervisor/</c>). The main file holds the flat turn
/// pipeline + the claim/execute steps; <c>SupervisorTurnService.Rehydrate.cs</c> holds the ledger fold.
/// </summary>
public sealed partial class SupervisorTurnService : ISupervisorTurnService, IScopedDependency
{
    private readonly ISupervisorDecisionLog _ledger;
    private readonly ISupervisorDecider _decider;
    private readonly ISupervisorActionExecutor _executor;
    private readonly ILogger<SupervisorTurnService> _logger;

    public SupervisorTurnService(ISupervisorDecisionLog ledger, ISupervisorDecider decider, ISupervisorActionExecutor executor, ILogger<SupervisorTurnService> logger)
    {
        _ledger = ledger;
        _decider = decider;
        _executor = executor;
        _logger = logger;
    }

    public async Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string goal, CancellationToken cancellationToken)
    {
        var context = await RehydrateFromDecisionLogAsync(supervisorRunId, teamId, goal, cancellationToken).ConfigureAwait(false);

        var decision = ChooseDecision(context);

        var outcomeJson = await ClaimAndExecuteAsync(supervisorRunId, teamId, context, decision, cancellationToken).ConfigureAwait(false);

        return BuildResult(context, decision, outcomeJson);
    }

    /// <summary>
    /// Pick the next decision. Budget is the FAIL-CLOSED gate, checked BEFORE asking the decider and counted
    /// from the durable ledger (<see cref="SupervisorTurnContext.TurnNumber"/> = decided-decision count) so it
    /// survives replay and can't be reset by re-entering the node: at/over <c>DecisionBudget</c> we force a
    /// terminal <c>stop</c> rather than emit one more decision. A re-entry after the budget tripped re-derives
    /// the same forced stop deterministically.
    /// </summary>
    private SupervisorDecision ChooseDecision(SupervisorTurnContext context)
    {
        if (context.TurnNumber >= SupervisorLane.DecisionBudget)
        {
            _logger.LogWarning("Supervisor decision budget exhausted at turn {Turn} (budget {Budget}) — forcing terminal stop", context.TurnNumber, SupervisorLane.DecisionBudget);

            return ForcedBudgetStop();
        }

        return _decider.Decide(context);
    }

    /// <summary>
    /// Claim + execute the decision EXACTLY ONCE behind the E1 ledger hops, or replay a prior outcome. The
    /// per-turn idempotency key (<see cref="DeriveDecisionKey"/>) makes the SAME decision in a later turn a
    /// distinct, re-executable row, and a re-derived key in the SAME turn collide on the unique index → the
    /// replay path. On Proceed we win the Pending → Running CAS (the must-fix-#2 single-winner gate) before
    /// the side effect; a lost begin-CAS (a concurrent racer won) or a Duplicate/InFlight claim replays the
    /// existing outcome rather than double-executing.
    /// </summary>
    private async Task<string?> ClaimAndExecuteAsync(Guid supervisorRunId, Guid teamId, SupervisorTurnContext context, SupervisorDecision decision, CancellationToken cancellationToken)
    {
        var idempotencyKey = DeriveDecisionKey(decision, context.TurnNumber);
        var inputHash = SupervisorDecisionLog.HashPayload(decision.PayloadJson);

        var claim = await _ledger.TryClaimAsync(supervisorRunId, teamId, decision.Kind, idempotencyKey, inputHash, decision.PayloadJson, fenceEpoch: context.TurnNumber, cancellationToken).ConfigureAwait(false);

        // Duplicate = a TERMINAL row already settled this turn's decision → replay its outcome, NEVER re-run
        // the side effect (the exactly-once replay path). Proceed (fresh INSERT) or InFlight (a prior turn
        // crashed after the claim INSERT but before recording terminal — re-enter it) BOTH go through the
        // Pending → Running CAS gate, which runs the side effect exactly once for the single winner.
        if (claim.Outcome == SupervisorDecisionClaimOutcome.Duplicate) return claim.PriorOutcomeJson;

        return await ExecuteUnderClaimAsync(claim.DecisionId, teamId, context, decision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Win the Pending → Running CAS, run the side effect ONCE, record the terminal; a lost CAS means a concurrent caller is executing — there's no prior terminal to read yet, so this turn re-enters next pass.</summary>
    private async Task<string?> ExecuteUnderClaimAsync(Guid decisionId, Guid teamId, SupervisorTurnContext context, SupervisorDecision decision, CancellationToken cancellationToken)
    {
        var won = await _ledger.TryBeginExecutionAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);

        if (!won)
        {
            _logger.LogInformation("Supervisor decision {DecisionId} already claimed for execution by a concurrent caller — not double-executing", decisionId);

            return null;
        }

        var outcomeJson = await _executor.ExecuteAsync(decision, context, cancellationToken).ConfigureAwait(false);

        await _ledger.RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Succeeded, outcomeJson, error: null, cancellationToken).ConfigureAwait(false);

        return outcomeJson;
    }

    /// <summary>Build the node's instruction: a terminal decision finishes the loop; anything else parks with the NEXT turn's context (this turn's decision folded in, so the next rehydrate sees TurnNumber+1).</summary>
    private static SupervisorTurnResult BuildResult(SupervisorTurnContext context, SupervisorDecision decision, string? outcomeJson)
    {
        if (decision.IsTerminal) return SupervisorTurnResult.Finished(decision.Kind, ReadStopReason(decision));

        var nextTurn = context with { TurnNumber = context.TurnNumber + 1, InFlight = null };

        return SupervisorTurnResult.Park(decision.Kind, nextTurn);
    }
}
