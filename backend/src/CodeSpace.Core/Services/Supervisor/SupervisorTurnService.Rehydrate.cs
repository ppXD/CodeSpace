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

        var priorDecisions = new List<SupervisorPriorDecision>();
        SupervisorPriorDecision? inFlight = null;

        // Walk the tape in Sequence order: a TERMINAL row is replayed (outcome only — its side effect is NOT
        // re-run), a non-terminal row is the one in-flight decision (a turn crashed after claim, before the
        // terminal record). TurnNumber = the count of DECIDED (terminal) decisions, which is what drives both
        // the next decision and the per-turn IterationKey — so a re-entry replays exactly the same decisions
        // and re-claims the in-flight one rather than emitting a duplicate.
        foreach (var row in rows)
        {
            var decision = ToPriorDecision(row);

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

    public async Task<int> CountPendingAgentWaitsAsync(Guid supervisorRunId, string nodeId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .CountAsync(w => w.RunId == supervisorRunId && w.NodeId == nodeId
                             && w.WaitKind == WorkflowWaitKinds.AgentRun && w.Status == WorkflowWaitStatuses.Pending, cancellationToken)
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
