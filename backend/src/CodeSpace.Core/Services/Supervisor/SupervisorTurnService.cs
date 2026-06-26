using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor.Arbiter;
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
    private readonly ISupervisorAcceptanceGrader _acceptanceGrader;
    private readonly IDecisionQueueService _decisionQueue;
    private readonly IDecisionArbiter _arbiter;
    private readonly IDecisionAnswerService _decisionAnswer;
    private readonly ILogger<SupervisorTurnService> _logger;

    public SupervisorTurnService(ISupervisorDecisionLog ledger, ISupervisorDecider decider, ISupervisorActionExecutor executor, CodeSpaceDbContext db, ISupervisorAcceptanceGrader acceptanceGrader, IDecisionQueueService decisionQueue, IDecisionArbiter arbiter, IDecisionAnswerService decisionAnswer, ILogger<SupervisorTurnService> logger)
    {
        _ledger = ledger;
        _decider = decider;
        _executor = executor;
        _db = db;
        _acceptanceGrader = acceptanceGrader;
        _decisionQueue = decisionQueue;
        _arbiter = arbiter;
        _decisionAnswer = decisionAnswer;
        _logger = logger;
    }

    public async Task<SupervisorTurnResult> RunTurnAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, Guid? conversationId, SupervisorGoalConfig? goalConfig, CancellationToken cancellationToken)
    {
        var plan = SupervisorGoalPlan.From(goalConfig);

        var context = (await RehydrateFromDecisionLogAsync(supervisorRunId, teamId, nodeId, goal, goalConfig, cancellationToken).ConfigureAwait(false))
            with { ConversationId = conversationId };

        if (context.InFlight != null) return await ReplayInFlightTurnAsync(teamId, context, cancellationToken).ConfigureAwait(false);

        var depth = await SupervisorDepthAsync(supervisorRunId, teamId, cancellationToken).ConfigureAwait(false);

        var decision = await ChooseDecisionAsync(context, plan, depth, cancellationToken).ConfigureAwait(false);

        var execution = await ClaimAndExecuteAsync(supervisorRunId, teamId, context, decision, cancellationToken).ConfigureAwait(false);

        return BuildResult(context, decision, execution);
    }

    /// <summary>
    /// Replay a crashed-mid-execution decision (<see cref="SupervisorTurnContext.InFlight"/>) FROZEN — the decider +
    /// bounds are NOT re-run. The in-flight row exists ONLY because the decision was already chosen, passed pre-bounds
    /// + post-gate, and claimed (INSERTed) in a prior walk that then crashed before recording terminal; re-judging it
    /// (re-asking the decider, re-running bounds) could force-stop or diverge from an already-committed decision, so
    /// replay must just FINISH it. This makes recovery INDEPENDENT OF DECIDER DETERMINISM (P1-1): a non-deterministic
    /// real LLM re-asked on the same turn would emit a DIFFERENT decision → a different idempotency key → a divergent
    /// 2nd ledger row + a stranded in-flight row. We re-enter execution DIRECTLY on the persisted row id
    /// (<c>InFlight.Id</c>) — SKIPPING TryClaim / DeriveDecisionKey entirely, so no key is derived on replay (which
    /// would otherwise break on the jsonb whitespace-normalization of the read-back payload).
    /// ask_human is terminal-on-park (its wait token is recorded via RecordTerminal), so InFlight is only ever a
    /// crashed plan / spawn / retry / merge / stop — never an ask_human awaiting an answer.
    /// </summary>
    private async Task<SupervisorTurnResult> ReplayInFlightTurnAsync(Guid teamId, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var decision = new SupervisorDecision { Kind = context.InFlight!.DecisionKind, PayloadJson = context.InFlight.PayloadJson };

        var execution = await ExecuteUnderClaimAsync(context.InFlight.Id, teamId, context, decision, cancellationToken).ConfigureAwait(false);

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
    internal async Task<SupervisorDecision> ChooseDecisionAsync(SupervisorTurnContext context, SupervisorGoalPlan plan, int depth, CancellationToken cancellationToken)
    {
        var preBound = SupervisorBounds.PreDecision(context, plan, depth);

        if (preBound != null) return ForcedStop(preBound);

        // D4c-2: BEFORE the delivery decider, drain this run's blocked child decisions — the arbiter auto-answers the
        // ones it is confident about (within the fail-closed floor) and leaves the rest in the cross-grain queue for a
        // human. A pure side-channel (it resolves CHILD-grain decisions, never the supervisor's own turn), so it always
        // falls through to the decider; skipped on a force-stopping run (it runs only past the pre-bound guard).
        await ArbitratePendingChildDecisionsAsync(context, cancellationToken).ConfigureAwait(false);

        var decision = await _decider.DecideAsync(context, cancellationToken).ConfigureAwait(false);

        decision = ClampSpawnToDependencyFrontier(context, decision);

        return ApplyPostDecisionGate(context, plan, decision);
    }

    /// <summary>
    /// Clamp a spawn decision to its dependency-ready frontier (loopability) BEFORE it is claimed + frozen, so the
    /// PERSISTED payload's <c>subtaskIds</c> match the agents that actually stage one-for-one — the positional
    /// <c>subtaskIds[i] ↔ agentResults[i]</c> invariant every downstream fold (per-unit acceptance, the dependency gate,
    /// the merge) relies on stays intact. A subtask whose <c>DependsOn</c> are not yet a non-rejected success is DROPPED;
    /// the model re-proposes it on a later turn once they settle (it sees the dependency frontier in the prompt). PURE +
    /// deterministic over the tape → a replay re-derives the identical clamp + idempotency key. A flat plan (no
    /// <c>DependsOn</c>) or an all-ready spawn returns the decision UNCHANGED (byte-identical, the dominant case). An
    /// all-deferred spawn clamps to an EMPTY fan-out (a zero-agent synchronous self-advance that trips the no-progress
    /// bound, so a cyclic / unsatisfiable DAG converges to a clean stop rather than looping).
    /// </summary>
    private SupervisorDecision ClampSpawnToDependencyFrontier(SupervisorTurnContext context, SupervisorDecision decision)
    {
        if (decision.Kind != SupervisorDecisionKinds.Spawn) return decision;

        SupervisorSpawnPayload? spawn;
        try { spawn = JsonSerializer.Deserialize<SupervisorSpawnPayload>(decision.PayloadJson, AgentJson.Options); }
        catch (JsonException) { return decision; }

        if (spawn is null) return decision;

        var (ready, deferred) = SupervisorDependencyGate.Partition(context, spawn.SubtaskIds);

        if (deferred.Count == 0) return decision;   // every requested subtask is ready → byte-identical (the dominant case)

        _logger.LogInformation("Supervisor deferred {Deferred} subtask(s) with unmet dependencies at turn {Turn} on node {NodeId}, clamping the spawn to {Ready} ready subtask(s)", deferred.Count, context.TurnNumber, context.NodeId, ready.Count);

        var readySet = ready.ToHashSet();

        var clamped = new SupervisorSpawnPayload
        {
            SubtaskIds = ready,
            Agents = spawn.Agents?.Where(a => readySet.Contains(a.SubtaskId)).ToList() is { Count: > 0 } kept ? kept : null,
        };

        return decision with { PayloadJson = JsonSerializer.Serialize(clamped, AgentJson.Options) };
    }

    /// <summary>
    /// Apply the per-decision bounds + governance to the decider's chosen decision. A bound breach FORCE-STOPs
    /// (fail-closed — no side effect). A governance verdict reshapes a side-effecting decision: Deny → force-STOP;
    /// RequireApproval → an ask_human approval card that gates the effect behind a human (reusing E4's HITL park);
    /// Allow → the decision proceeds unchanged. The post-decision spawn-count bound is checked FIRST so an
    /// over-cap spawn stops cleanly even under an approval policy.
    /// </summary>
    internal SupervisorDecision ApplyPostDecisionGate(SupervisorTurnContext context, SupervisorGoalPlan plan, SupervisorDecision decision)
    {
        // Tier-0 FIRST: a structurally invalid plan (a dangling DependsOn ref or a cycle) force-STOPs at plan time —
        // before the dependency gate would otherwise defer its subtasks forever into a no-progress stall. Structure is
        // more fundamental than the fan-out count, so it precedes the bound (a plan that is BOTH malformed and over-cap
        // reports PlanInvalid first). Pure → a re-entry re-derives the same stop.
        var planInvalid = SupervisorPlanValidator.Validate(decision);

        if (planInvalid != null) return ForcedStop(planInvalid);

        var postBound = SupervisorBounds.PostDecision(context, plan, decision);

        if (postBound != null) return ForcedStop(postBound);

        return GateSideEffectingDecision(context, decision);
    }

    /// <summary>
    /// Route a SIDE-EFFECTING decision through the governance gate (PR-E E5, Rule 7 — reuses
    /// <see cref="SupervisorGovernance"/> over <c>AgentToolGate</c>): Allow → unchanged; Deny → fail-closed
    /// force-STOP (no side effect, recorded reason); RequireApproval → rewrite into an ask_human APPROVAL card
    /// that parks for a human before any agent is created (reusing E4's durable HITL park) — UNLESS this decision
    /// was JUST approved (the immediately-preceding decided decision is this gate's own approval card with an
    /// approving answer), in which case the approval is bound to it and it PROCEEDS once (approve-then-proceed,
    /// not a permanent block). A non-side-effecting decision (plan / merge / stop / ask_human) is Allow and passes
    /// through unchanged.
    /// </summary>
    internal SupervisorDecision GateSideEffectingDecision(SupervisorTurnContext context, SupervisorDecision decision)
    {
        var verdict = SupervisorGovernance.Decide(decision.Kind, context.ApprovalPolicy, irreversible: SupervisorGovernance.IsIrreversible(decision.Kind));

        if (verdict == AgentToolGateDecision.Allow) return decision;

        // Approve-then-proceed: a side effect a human just approved is bound to its approval and runs ONCE,
        // rather than being re-gated into another ask_human (which would loop the run to a no-progress / budget
        // force-STOP and never execute the approved spawn).
        if (verdict == AgentToolGateDecision.RequireApproval && SupervisorApprovalRequest.WasJustApproved(context))
        {
            _logger.LogInformation("Supervisor governance approval was granted for a {Kind} decision at turn {Turn} (policy {Policy}) — proceeding with the human-approved side effect", decision.Kind, context.TurnNumber, context.ApprovalPolicy);

            return decision;
        }

        // Intentionally unreachable from operator config today (ParseApprovalPolicy maps every unknown policy to None) — fail-closed defense-in-depth for the future irreversible/merge-PR path; the Deny→GovernanceDenied force-stop wiring is driven end-to-end by SupervisorTurnServiceTests.A_governance_denied_side_effecting_decision_force_stops_and_stages_no_agent.
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

        var execution = await ExecuteOrTerminalizeFailureAsync(decisionId, teamId, context, decision, cancellationToken).ConfigureAwait(false);

        // L4 P1: a terminal stop carrying a MODEL-authored acceptance check is graded HERE — inline on the decided-stop
        // path, BEFORE the terminal is recorded — so the folded verdict is persisted with the outcome and read off it at
        // BuildResult (a stop finishes the same turn; it never re-rehydrates, so the resolve's fold-at-rehydrate seam
        // does not apply). The duplicate-claim replay (ClaimAndExecuteAsync) returns the already-graded outcome, so the
        // grade I/O runs at most once per committed stop; a no-acceptance stop is a byte-identical no-op.
        execution = await ApplyStopAcceptanceGradeAsync(execution, context, decision, teamId, cancellationToken).ConfigureAwait(false);

        await _ledger.RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Succeeded, execution.OutcomeJson, error: null, cancellationToken).ConfigureAwait(false);

        return execution;
    }

    /// <summary>
    /// Run the side effect, but if a spawn/retry references an unresolvable persona (missing / foreign / corrupt
    /// — <see cref="AgentDefinitionResolutionException"/>, which the executor prefixes for the supervisor lane) OR a
    /// model-authored per-agent dispatch escapes the repo privilege gate (<see cref="SupervisorRepoAccessException"/>,
    /// L4 arc B — an out-of-set or access-escalating repo), record the decision as a terminal FAILURE before
    /// re-throwing. Without this the exception would escape with the row left stranded <c>Running</c> (the terminal
    /// record below never runs), and a re-walk would re-enter the same in-flight decision and re-throw forever.
    /// Recording Failed here makes it a CLEAN, terminal node failure (the node's <c>RunAsync</c> surfaces the re-thrown
    /// message → node retry + the <c>error</c> branch compose), mirroring <c>WorkflowEngine.StageAgentRunAsync</c> for an
    /// <c>agent.code</c> node.
    /// </summary>
    private async Task<SupervisorExecution> ExecuteOrTerminalizeFailureAsync(Guid decisionId, Guid teamId, SupervisorTurnContext context, SupervisorDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            return await _executor.ExecuteAsync(decision, context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentDefinitionResolutionException or SupervisorRepoAccessException or SupervisorModelAccessException)
        {
            await _ledger.RecordTerminalAsync(decisionId, teamId, SupervisorDecisionStatus.Failed, outcomeJson: null, error: ex.Message, cancellationToken).ConfigureAwait(false);

            throw;
        }
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
    internal static SupervisorTurnResult BuildResult(SupervisorTurnContext context, SupervisorDecision decision, SupervisorExecution execution)
    {
        if (decision.IsTerminal)
        {
            // L4 P1: the objective acceptance verdict folded onto THIS stop's outcome (null = no model check authored).
            // A FAILED model definition-of-done WITHHOLDS the reviewable head — there is no verified branch to ship, so a
            // downstream git.open_pr / git.open_change_set sees nothing (exactly as a non-verified resolve withholds its
            // branch). null/true surface the branches as before (byte-identical for every run with no model acceptance).
            var acceptancePassed = SupervisorOutcome.ReadAcceptanceGradePassed(execution.OutcomeJson);
            var withhold = acceptancePassed == false;

            return SupervisorTurnResult.Finished(
                decision.Kind,
                ReadStopReason(decision),
                withhold ? null : SupervisorOutcome.ReadFinalIntegratedBranch(context.PriorDecisions),
                withhold ? Array.Empty<SupervisorRepositoryBranch>() : SupervisorOutcome.ReadFinalRepositoryBranches(context.PriorDecisions),
                acceptancePassed);
        }

        var nextTurn = context with { TurnNumber = context.TurnNumber + 1, InFlight = null };

        if (execution.HumanWaitToken != null) return SupervisorTurnResult.ParkOnHuman(decision.Kind, nextTurn, execution.HumanWaitToken);

        return execution.ParkedAgentWaitCount > 0
            ? SupervisorTurnResult.ParkOnAgents(decision.Kind, nextTurn, execution.ParkedAgentWaitCount)
            : SupervisorTurnResult.SelfAdvance(decision.Kind, nextTurn);
    }
}
