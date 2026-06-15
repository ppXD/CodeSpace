using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The replay-fold half of the turn service (Rule 10 <c>.Rehydrate.cs</c>): reads the durable decision tape
/// and derives the per-turn idempotency key. Mirrors the engine's <c>RehydrateMapResultsAsync</c> — replay
/// the settled rows, identify the unsettled one — but per-DECISION rather than per-branch.
/// </summary>
public sealed partial class SupervisorTurnService
{
    public async Task<SupervisorTurnContext> RehydrateFromDecisionLogAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, SupervisorGoalConfig? goalConfig, CancellationToken cancellationToken)
    {
        var rows = await _ledger.GetForRunAsync(supervisorRunId, teamId, cancellationToken).ConfigureAwait(false);

        // The human answers to this run's ask_human turns, keyed by the question card's token. The answer rides
        // the resolved Action wait's payload (the human's free-text comment), so the FOLD reads it durably from
        // the wait on EVERY rehydrate — surviving restart — rather than relying on a separate write into the
        // ledger row. An ask_human decision whose wait isn't yet resolved has no entry → answer stays null.
        // Only hit the DB when the tape actually has an ask_human decision — the common (no-ask) run stays a
        // single ledger read, byte-identical to E3 and DB-free.
        var answersByToken = rows.Any(r => r.DecisionKind == SupervisorDecisionKinds.AskHuman)
            ? await ResolvedAskAnswersByTokenAsync(supervisorRunId, nodeId, cancellationToken).ConfigureAwait(false)
            : EmptyAnswers;

        var priorDecisions = new List<SupervisorPriorDecision>();
        SupervisorPriorDecision? inFlight = null;

        // Walk the tape in Sequence order: a TERMINAL row is replayed (outcome only — its side effect is NOT
        // re-run), a non-terminal row is the one in-flight decision (a turn crashed after claim, before the
        // terminal record). TurnNumber = the count of DECIDED (terminal) decisions, which is what drives both
        // the next decision and the per-turn IterationKey — so a re-entry replays exactly the same decisions
        // and re-claims the in-flight one rather than emitting a duplicate.
        foreach (var row in rows)
        {
            var decision = FoldAskHumanAnswer(ToPriorDecision(row), answersByToken);

            // Persist a newly-folded answer onto the durable ledger row (the answer becomes the ledger's record,
            // surviving restart without re-reading the wait). Idempotent — the update no-ops when the bytes match.
            if (decision.OutcomeJson != row.OutcomeJson)
                await _ledger.UpdateOutcomeAsync(row.Id, teamId, decision.OutcomeJson!, cancellationToken).ConfigureAwait(false);

            if (SupervisorDecisionStateMachine.IsTerminal(row.Status))
                priorDecisions.Add(decision);
            else
                inFlight = decision;
        }

        return new SupervisorTurnContext
        {
            Goal = goal,
            SupervisorRunId = supervisorRunId,
            TeamId = teamId,
            NodeId = nodeId,
            TurnNumber = priorDecisions.Count,
            PriorDecisions = priorDecisions,
            InFlight = inFlight,
            TotalSpawnedAgents = FoldTotalSpawnedAgents(priorDecisions),
            NoProgressDecisions = FoldNoProgressDecisions(priorDecisions),
            ApprovalPolicy = SupervisorGoalPlan.From(goalConfig).ApprovalPolicy,
        };
    }

    /// <summary>
    /// Sum the agents this run has spawned so far from the DURABLE ledger — every prior <c>spawn</c> / <c>retry</c>
    /// decision's recorded <c>agentCount</c> (the E5 total-spawn cap counter). A LEDGER FACT, so it survives replay
    /// + can't be reset by re-entering the node: a re-entry re-reads the same settled spawn outcomes and re-derives
    /// the same total, so the cap can't be sidestepped by restarting.
    /// </summary>
    private static int FoldTotalSpawnedAgents(IReadOnlyList<SupervisorPriorDecision> priorDecisions) =>
        priorDecisions
            .Where(d => d.DecisionKind is SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry)
            .Sum(d => SupervisorOutcome.ReadStagedAgentCount(d.OutcomeJson));

    /// <summary>
    /// Count the MOST RECENT consecutive decisions that produced no new SETTLED agent result (the E5 best-effort
    /// no-progress counter, folded from the durable ledger). A decision "made progress" if it staged agents whose
    /// results a later turn can fold (spawn/retry with agents) OR it is a merge that read prior results; a run of
    /// decisions that spawned nothing + merged nothing (e.g. the decider looping on plan / a degraded ask_human)
    /// accumulates. A spawn/retry resets the counter (it advanced the work). DEMOTED to best-effort per the design
    /// — a long-running spawn whose agents haven't settled is a PARK (not a fresh decided turn), so it never trips.
    /// </summary>
    private static int FoldNoProgressDecisions(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var streak = 0;

        foreach (var decision in priorDecisions)
            streak = MadeProgress(decision) ? 0 : streak + 1;

        return streak;
    }

    /// <summary>A decision advanced the work if it spawned/retried agents (the work fans out) or merged prior results (it consumed them). Plan / ask_human / a zero-agent spawn make no fresh progress toward a settled result.</summary>
    private static bool MadeProgress(SupervisorPriorDecision decision) => decision.DecisionKind switch
    {
        SupervisorDecisionKinds.Spawn or SupervisorDecisionKinds.Retry => SupervisorOutcome.ReadStagedAgentCount(decision.OutcomeJson) > 0,
        SupervisorDecisionKinds.Merge => true,
        _ => false,
    };

    /// <summary>
    /// Fold the human's answer into an ask_human decision's replayed outcome (E4): look up the recorded
    /// question-card token in the resolved-Action-wait answers, and — when the human has replied — rewrite the
    /// decision's <c>OutcomeJson</c> with the answer so the decider sees "you asked X, the human answered Y" on
    /// the next turn. A non-ask_human decision, or an ask_human whose wait isn't yet resolved (no token match),
    /// passes through unchanged. The fold is idempotent — re-running it on an already-folded outcome is a no-op.
    /// </summary>
    private static SupervisorPriorDecision FoldAskHumanAnswer(SupervisorPriorDecision decision, IReadOnlyDictionary<string, string> answersByToken)
    {
        if (decision.DecisionKind != SupervisorDecisionKinds.AskHuman) return decision;

        var token = SupervisorOutcome.ReadHumanWaitToken(decision.OutcomeJson);

        if (token == null || !answersByToken.TryGetValue(token, out var answer)) return decision;

        var folded = SupervisorOutcome.FoldAnswer(SupervisorOutcome.ReadAskHumanQuestion(decision.OutcomeJson), token, answer);

        return decision with { OutcomeJson = folded };
    }

    /// <summary>
    /// The human answer to each resolved ask_human turn, keyed by the question card's correlation token. Read
    /// from the durable resolved <c>Action</c> waits this node staged: the wait's resolved payload is
    /// <c>{ action, by, comment }</c> and the answer is the human's free-text <c>comment</c>. Only RESOLVED
    /// waits contribute (a still-pending ask hasn't been answered), so the fold writes the answer at most once
    /// it durably exists, and re-reads it identically on every restart.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolvedAskAnswersByTokenAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken)
    {
        var resolved = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == supervisorRunId && w.NodeId == nodeId
                        && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Resolved
                        && w.IterationKey.EndsWith("#ask"))
            .Select(w => new { w.Token, w.PayloadJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var answers = new Dictionary<string, string>();

        foreach (var wait in resolved)
            answers[wait.Token] = SupervisorOutcome.ReadAnswerComment(wait.PayloadJson);

        return answers;
    }

    /// <summary>The shared empty answers map for the common no-ask_human rehydrate — keeps that path allocation-light + DB-free.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyAnswers = new Dictionary<string, string>();

    /// <summary>
    /// How many WorkflowRun ancestors this supervisor run already has (PR-E E5 depth cap) — walks the
    /// <c>parent_run_id</c> chain exactly as <c>SubworkflowService.EnsureWithinDepthAsync</c> does, bounded by
    /// <see cref="SupervisorLane.MaxSupervisorDepth"/> so a corrupt cycle can't loop forever. The pre-decision
    /// depth bound force-STOPs a supervisor nested beyond this many ancestors (a recursive supervisor-spawns-
    /// supervisor fan-out), at turn 0, before it can spawn. A top-level supervisor (no parent) reads depth 0.
    /// </summary>
    public async Task<int> SupervisorDepthAsync(Guid supervisorRunId, Guid teamId, CancellationToken cancellationToken)
    {
        // A pure-unit context (no DbContext) has no run hierarchy → depth 0 (top-level), so the depth bound
        // never trips a db-less test. The real engine always supplies _db; only fakes pass db: null!.
        if (_db == null) return 0;

        var depth = 0;

        var cursor = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == supervisorRunId && r.TeamId == teamId)
            .Select(r => r.ParentRunId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        while (cursor.HasValue && depth < SupervisorLane.MaxSupervisorDepth)
        {
            depth++;

            cursor = await _db.WorkflowRun.AsNoTracking()
                .Where(r => r.Id == cursor.Value)
                .Select(r => r.ParentRunId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        return depth;
    }

    public async Task<int> CountPendingAgentWaitsAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .CountAsync(w => w.RunId == supervisorRunId && w.NodeId == nodeId
                             && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending, cancellationToken)
            .ConfigureAwait(false);

    public async Task<string?> PendingHumanWaitTokenAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == supervisorRunId && w.NodeId == nodeId
                        && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Pending && w.IterationKey.EndsWith("#ask"))
            .Select(w => w.Token)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static SupervisorPriorDecision ToPriorDecision(Persistence.Entities.SupervisorDecisionRecord row) => new()
    {
        Sequence = row.Sequence,
        DecisionKind = row.DecisionKind,
        Status = row.Status,
        PayloadJson = row.PayloadJson,
        OutcomeJson = row.OutcomeJson,
        Error = row.Error,
    };

    /// <summary>
    /// Server-derived per-TURN idempotency key (must-fix #1's exactly-once partner): bind the decision's
    /// canonical payload to a <c>turn{N}</c> discriminator so the SAME decision in a later turn is a DISTINCT,
    /// re-executable ledger row (no unique-index collision across turns), while a re-derived key in the SAME
    /// turn collides → the replay path. Never read from any model — the inputs (kind + payload + turn) are
    /// all server-side.
    /// </summary>
    private static string DeriveDecisionKey(SupervisorDecision decision, int turnNumber) =>
        SupervisorDecisionLog.DeriveIdempotencyKey(decision.Kind, decision.PayloadJson, TurnDiscriminator(turnNumber));

    /// <summary>The per-turn discriminator <c>turn{N}</c> — the ledger-key analogue of the wait's IterationKey turn segment.</summary>
    internal static string TurnDiscriminator(int turnNumber) => $"turn{turnNumber}";

    /// <summary>
    /// The forced terminal decision when a fail-closed bound or governance refusal trips (PR-E E2/E5) — a
    /// <c>stop</c> stamping the DISTINCT <paramref name="reason"/> (a <see cref="SupervisorStopReasons"/> value).
    /// DETERMINISTIC given (reason): a re-entry after the same bound tripped re-derives the identical stop, so the
    /// per-turn idempotency key is stable and the run terminates cleanly with the operator-legible reason.
    /// </summary>
    private static SupervisorDecision ForcedStop(string reason) => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new { reason }, AgentJson.Options),
    };

    /// <summary>The terminal reason stamped on a budget-forced stop (back-compat alias for the E2 reason; points at the E5 vocabulary). Surfaced as the node's terminal reason.</summary>
    public const string BudgetExhaustedReason = SupervisorStopReasons.BudgetExhausted;

    /// <summary>Read the <c>reason</c> from a stop decision's payload for the node's terminal output (best-effort; null when absent/malformed).</summary>
    private static string? ReadStopReason(SupervisorDecision decision)
    {
        try
        {
            var root = JsonDocument.Parse(decision.PayloadJson).RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
