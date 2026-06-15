using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// The replay-fold half of the turn service (Rule 10 <c>.Rehydrate.cs</c>): reads the durable decision tape
/// and derives the per-turn idempotency key. Mirrors the engine's <c>RehydrateMapResultsAsync</c> — replay
/// the settled rows, identify the unsettled one — but per-DECISION rather than per-branch.
/// </summary>
public sealed partial class SupervisorTurnService
{
    public async Task<SupervisorTurnContext> RehydrateFromDecisionLogAsync(Guid supervisorRunId, Guid teamId, string nodeId, string goal, CancellationToken cancellationToken)
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
        };
    }

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

    /// <summary>The forced terminal decision when the budget is exhausted — a <c>stop</c> with a fixed "budget exhausted" reason, deterministic so a re-entry after the budget tripped re-derives it identically.</summary>
    private static SupervisorDecision ForcedBudgetStop() => new()
    {
        Kind = SupervisorDecisionKinds.Stop,
        PayloadJson = JsonSerializer.Serialize(new { reason = BudgetExhaustedReason }, AgentJson.Options),
    };

    /// <summary>The terminal reason stamped on a budget-forced stop. Surfaced as the node's terminal reason.</summary>
    public const string BudgetExhaustedReason = "budget exhausted";

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
