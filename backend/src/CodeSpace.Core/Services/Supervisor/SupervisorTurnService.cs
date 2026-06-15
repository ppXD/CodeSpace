using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
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
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<SupervisorTurnService> _logger;

    public SupervisorTurnService(ISupervisorDecisionLog ledger, ISupervisorDecider decider, ISupervisorActionExecutor executor, CodeSpaceDbContext db, ILogger<SupervisorTurnService> logger)
    {
        _ledger = ledger;
        _decider = decider;
        _executor = executor;
        _db = db;
        _logger = logger;
    }

    public async Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, Guid? conversationId, SupervisorGoalConfig? goalConfig, CancellationToken cancellationToken)
    {
        var plan = SupervisorGoalPlan.From(goalConfig);

        var context = (await RehydrateFromDecisionLogAsync(supervisorRunId, teamId, nodeId, goal, goalConfig, cancellationToken).ConfigureAwait(false))
            with { ConversationId = conversationId };

        var depth = await SupervisorDepthAsync(supervisorRunId, teamId, cancellationToken).ConfigureAwait(false);

        var decision = await ChooseDecisionAsync(context, plan, depth, cancellationToken).ConfigureAwait(false);

        var execution = await ClaimAndExecuteAsync(supervisorRunId, teamId, context, decision, cancellationToken).ConfigureAwait(false);

        return BuildResult(context, decision, execution);
    }

    /// <summary>
    /// Pick the next decision behind the fail-closed bounds + governance gate, all counted from the DURABLE
    /// ledger (never an in-memory tally) so they survive replay + can't be reset by re-entering the node. The
    /// order is deterministic so a re-entry re-derives the SAME forced stop:
    /// <list type="number">
    ///   <item>PRE-DECISION bounds (the run can't even ask for one more decision): depth cap (a supervisor nested
    ///         beyond <c>MaxSupervisorDepth</c> supervisor-ancestors), round budget (<c>TurnNumber</c> ≥ the run's
    ///         <c>MaxRounds</c>), best-effort no-progress (consecutive no-new-result decisions ≥ the cap). Each
    ///         FORCE-STOPs with a distinct terminal reason instead of asking the decider.</item>
    ///   <item>ask the decider for the next decision;</item>
    ///   <item>POST-DECISION bounds + GOVERNANCE on the chosen decision: a spawn whose K exceeds the per-decision
    ///         fan-out cap, or whose total would breach <c>MaxTotalSpawns</c>, is REFUSED → force-STOP; a
    ///         side-effecting decision the governance gate DENIES is refused → force-STOP; one it RequireApproves
    ///         is rewritten into an ask_human approval park (the human's answer gates the next turn) — never an
    ///         ungated side effect.</item>
    /// </list>
    /// </summary>
    private async Task<SupervisorDecision> ChooseDecisionAsync(SupervisorTurnContext context, SupervisorGoalPlan plan, int depth, CancellationToken cancellationToken)
    {
        var preBound = SupervisorBounds.PreDecision(context, plan, depth);

        if (preBound != null) return ForcedStop(preBound);

        var decision = await _decider.DecideAsync(context, cancellationToken).ConfigureAwait(false);

        return ApplyPostDecisionGate(context, plan, decision);
    }

    /// <summary>
    /// Apply the per-decision bounds + governance to the decider's chosen decision. A bound breach FORCE-STOPs
    /// (fail-closed — no side effect). A governance verdict reshapes a side-effecting decision: Deny → force-STOP;
    /// RequireApproval → an ask_human approval card that gates the effect behind a human (reusing E4's HITL park);
    /// Allow → the decision proceeds unchanged. The post-decision spawn-count bound is checked FIRST so an
    /// over-cap spawn stops cleanly even under an approval policy.
    /// </summary>
    private SupervisorDecision ApplyPostDecisionGate(SupervisorTurnContext context, SupervisorGoalPlan plan, SupervisorDecision decision)
    {
        var postBound = SupervisorBounds.PostDecision(context, plan, decision);

        if (postBound != null) return ForcedStop(postBound);

        return GateSideEffectingDecision(context, decision);
    }

    /// <summary>
    /// Route a SIDE-EFFECTING decision through the governance gate (PR-E E5, Rule 7 — reuses
    /// <see cref="SupervisorGovernance"/> over <c>AgentToolGate</c>): Allow → unchanged; Deny → fail-closed
    /// force-STOP (no side effect, recorded reason); RequireApproval → rewrite into an ask_human APPROVAL card
    /// that parks for a human before any agent is created (reusing E4's durable HITL park). A non-side-effecting
    /// decision (plan / merge / stop / ask_human) is Allow and passes through unchanged.
    /// </summary>
    private SupervisorDecision GateSideEffectingDecision(SupervisorTurnContext context, SupervisorDecision decision)
    {
        var verdict = SupervisorGovernance.Decide(decision.Kind, context.ApprovalPolicy);

        if (verdict == AgentToolGateDecision.Allow) return decision;

        if (verdict == AgentToolGateDecision.Deny)
        {
            _logger.LogWarning("Supervisor governance DENIED a {Kind} decision at turn {Turn} (policy {Policy}) — forcing terminal stop", decision.Kind, context.TurnNumber, context.ApprovalPolicy);

            return ForcedStop(SupervisorStopReasons.GovernanceDenied);
        }

        _logger.LogInformation("Supervisor governance requires approval for a {Kind} decision at turn {Turn} (policy {Policy}) — parking for a human before the side effect", decision.Kind, context.TurnNumber, context.ApprovalPolicy);

        return SupervisorApprovalRequest.IntoAskHuman(decision);
    }

    /// <summary>
    /// Claim + execute the decision EXACTLY ONCE behind the E1 ledger hops, or replay a prior outcome. The
    /// per-turn idempotency key (<see cref="DeriveDecisionKey"/>) makes the SAME decision in a later turn a
    /// distinct, re-executable row, and a re-derived key in the SAME turn collide on the unique index → the
    /// replay path. On Proceed we win the Pending → Running CAS (the must-fix-#2 single-winner gate) before
    /// the side effect; a lost begin-CAS (a concurrent racer won) or a Duplicate/InFlight claim replays the
    /// existing outcome rather than double-executing.
    /// </summary>
    private async Task<SupervisorExecution> ClaimAndExecuteAsync(Guid supervisorRunId, Guid teamId, SupervisorTurnContext context, SupervisorDecision decision, CancellationToken cancellationToken)
    {
        var idempotencyKey = DeriveDecisionKey(decision, context.TurnNumber);
        var inputHash = SupervisorDecisionLog.HashPayload(decision.PayloadJson);

        var claim = await _ledger.TryClaimAsync(supervisorRunId, teamId, decision.Kind, idempotencyKey, inputHash, decision.PayloadJson, fenceEpoch: context.TurnNumber, cancellationToken).ConfigureAwait(false);

        // Duplicate = a TERMINAL row already settled this turn's decision → REPLAY: never re-run the side
        // effect (the exactly-once-spawn guarantee — a spawn turn that already staged its K agent runs does NOT
        // re-stage). The replay still classifies the SAME suspend path the original did, by re-deriving the
        // staged-agent-wait count from the recorded outcome (so the node re-suspends on the EXISTING K waits
        // rather than self-advancing). Proceed (fresh INSERT) or InFlight (a turn crashed after the claim INSERT
        // but before recording terminal — re-enter it) BOTH go through the Pending → Running CAS gate, which
        // runs the side effect exactly once for the single winner.
        if (claim.Outcome == SupervisorDecisionClaimOutcome.Duplicate)
            return ReplayExecution(claim.PriorOutcomeJson);

        return await ExecuteUnderClaimAsync(claim.DecisionId, teamId, context, decision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Win the Pending → Running CAS, run the side effect ONCE, record the terminal. A LOST begin-CAS is the
    /// CRASH-RECOVERY path, NOT a concurrent racer: the engine's run-level Enqueued → Running single-writer claim
    /// means no second walk executes this run concurrently, so a row already past Pending here was flipped Running
    /// by a PRIOR walk that crashed before recording terminal (e.g. mid spawn fan-out — orphan agents staged, no
    /// waits, decision stuck Running). RE-EXECUTE under the existing Running claim so the turn doesn't self-advance
    /// past an unfinished decision; the executor's spawn staging is idempotent (it reclaims this turn's orphan
    /// agents), so the recovery produces exactly K agents + K waits with no double-spawn.
    /// </summary>
    private async Task<SupervisorExecution> ExecuteUnderClaimAsync(Guid decisionId, Guid teamId, SupervisorTurnContext context, SupervisorDecision decision, CancellationToken cancellationToken)
    {
        var won = await _ledger.TryBeginExecutionAsync(decisionId, teamId, cancellationToken).ConfigureAwait(false);

        if (!won)
            _logger.LogWarning("Supervisor decision {DecisionId} was already Running (a prior walk crashed before recording terminal) — re-executing to recover, not self-advancing", decisionId);

        var execution = await _executor.ExecuteAsync(decision, context, cancellationToken).ConfigureAwait(false);

        await _ledger.RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Succeeded, execution.OutcomeJson, error: null, cancellationToken).ConfigureAwait(false);

        return execution;
    }

    /// <summary>Reconstruct the suspend classification of a replayed (already-settled) decision from its recorded outcome — an ask_human outcome records its Action-wait token (re-park on the human's answer), a spawn/retry outcome records its staged agent-run ids (re-suspend on the SAME count of existing waits); everything else is a synchronous self-advance.</summary>
    private static SupervisorExecution ReplayExecution(string? priorOutcomeJson)
    {
        var outcome = priorOutcomeJson ?? "{}";

        var humanToken = SupervisorOutcome.ReadHumanWaitToken(outcome);

        if (humanToken != null) return SupervisorExecution.ParkedOnHuman(outcome, humanToken);

        var staged = SupervisorOutcome.ReadStagedAgentCount(outcome);

        return staged > 0 ? SupervisorExecution.ParkedOnAgents(outcome, staged) : SupervisorExecution.Synchronous(outcome);
    }

    /// <summary>
    /// Build the node's instruction (the three resume paths): a terminal decision FINISHES; an async agent
    /// decision (spawn / retry — the executor staged K agent waits) tells the node to PARK ON THOSE waits (the
    /// barrier resumes); an ask_human decision tells the node to PARK ON THE HUMAN's answer (a single Action
    /// wait); a synchronous non-terminal decision (plan / merge) SELF-ADVANCES on a SupervisorDecision wait. The
    /// next-turn context folds this turn's decision in, so the next rehydrate sees TurnNumber+1.
    /// </summary>
    private static SupervisorTurnResult BuildResult(SupervisorTurnContext context, SupervisorDecision decision, SupervisorExecution execution)
    {
        if (decision.IsTerminal) return SupervisorTurnResult.Finished(decision.Kind, ReadStopReason(decision));

        var nextTurn = context with { TurnNumber = context.TurnNumber + 1, InFlight = null };

        if (execution.HumanWaitToken != null) return SupervisorTurnResult.ParkOnHuman(decision.Kind, nextTurn, execution.HumanWaitToken);

        return execution.ParkedAgentWaitCount > 0
            ? SupervisorTurnResult.ParkOnAgents(decision.Kind, nextTurn, execution.ParkedAgentWaitCount)
            : SupervisorTurnResult.SelfAdvance(decision.Kind, nextTurn);
    }
}
