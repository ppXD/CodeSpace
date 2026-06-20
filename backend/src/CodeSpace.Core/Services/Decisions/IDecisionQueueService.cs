using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Decisions;

/// <summary>
/// The cross-grain "Needs decision" queue (Decision substrate D3): the UNIFIED read of every PENDING decision a team
/// owns, projected over BOTH park backends without special-casing either — an <c>agent.code</c> mid-run
/// <c>decision.request</c> (a parked tool-ledger row) and a <c>flow.decision</c> node (a Pending workflow-run wait).
/// Each backend stashes the same <c>DecisionRequest</c> envelope at park (the ledger's <c>decision_envelope_jsonb</c>
/// column / the wait's <c>payload_jsonb</c>), so the projection is one shared mapper. Team-scoped: a foreign team's
/// decisions never appear. Owns its DbContext reads (Rule 16); the query handler is a thin dispatcher.
/// </summary>
public interface IDecisionQueueService
{
    Task<IReadOnlyList<PendingDecision>> ListPendingAsync(Guid teamId, CancellationToken cancellationToken);
}

public sealed class DecisionQueueService : IDecisionQueueService, IScopedDependency
{
    // The envelope is serialized Web-default (camelCase) on both backends; DecisionRequest's fields are all strings, so
    // no enum converter is needed to round-trip it.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CodeSpaceDbContext _db;

    public DecisionQueueService(CodeSpaceDbContext db) => _db = db;

    public async Task<IReadOnlyList<PendingDecision>> ListPendingAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var agentRows = await ReadAgentGrainAsync(teamId, cancellationToken).ConfigureAwait(false);
        var nodeRows = await ReadNodeGrainAsync(teamId, cancellationToken).ConfigureAwait(false);

        var pending = new List<PendingDecision>(agentRows.Count + nodeRows.Count);

        foreach (var r in agentRows) Append(pending, Project(r.Id, r.CreatedDate, r.ApprovalMessageId, r.DecisionEnvelopeJson));
        foreach (var r in nodeRows) Append(pending, Project(r.Id, r.CreatedAt, answerMessageId: null, r.PayloadJson));

        // Soonest-deadline first (an unbounded one sorts last), then oldest — the operator triages the most urgent first.
        return pending.OrderBy(p => p.DeadlineAt ?? DateTimeOffset.MaxValue).ThenBy(p => p.CreatedAt).ToList();
    }

    /// <summary>Agent-grain: parked <c>decision.request</c> ledger rows (AwaitingApproval, never approved — a decision has no Running hop), the envelope stashed at park.</summary>
    private async Task<List<AgentDecisionRow>> ReadAgentGrainAsync(Guid teamId, CancellationToken cancellationToken) =>
        await _db.ToolCallLedger.AsNoTracking()
            .Where(l => l.TeamId == teamId && l.ToolKind == DecisionToolKinds.DecisionRequest
                && l.Status == ToolCallLedgerStatus.AwaitingApproval && l.ApprovedAt == null)
            .Select(l => new AgentDecisionRow(l.Id, l.CreatedDate, l.ApprovalMessageId, l.DecisionEnvelopeJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Node-grain: Pending <c>flow.decision</c> waits, team via the owning run; the envelope is stashed in the wait's payload_jsonb while Pending (overwritten by the answer only on resolve). Like the agent grain, it is REDACTED at park — <c>FlowDecisionNode</c> builds the envelope from the engine's redacted config, so a secret ref in decision text is a "[REDACTED: path]" marker here.</summary>
    private async Task<List<NodeDecisionRow>> ReadNodeGrainAsync(Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.WaitKind == WorkflowWaitKinds.Decision && w.Status == WorkflowWaitStatuses.Pending
                && _db.WorkflowRun.Any(r => r.Id == w.RunId && r.TeamId == teamId))
            .Select(w => new NodeDecisionRow(w.Id, w.CreatedAt, w.PayloadJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static void Append(List<PendingDecision> sink, PendingDecision? item)
    {
        if (item is not null) sink.Add(item);
    }

    /// <summary>The ONE shared projection both backends funnel through: deserialize the stashed envelope and map it to a queue item. A missing / malformed envelope yields null (skipped) — a queue can never crash on one bad row. Internal for direct unit testing.</summary>
    internal static PendingDecision? Project(Guid id, DateTimeOffset createdAt, Guid? answerMessageId, string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson)) return null;

        DecisionRequest? env;
        try { env = JsonSerializer.Deserialize<DecisionRequest>(envelopeJson, Json); }
        catch (JsonException) { return null; }

        if (env is null) return null;

        return new PendingDecision
        {
            Id = id,
            Grain = env.ResumeBackend,
            RootTraceId = env.RootTraceId,
            WorkflowRunId = env.WorkflowRunId,
            AgentRunId = env.AgentRunId,
            NodeId = env.NodeId,
            DecisionType = env.DecisionType,
            Question = env.Question,
            Options = env.Options,
            RecommendedOption = env.RecommendedOption,
            BlockingReason = env.BlockingReason,
            ContextSummary = env.ContextSummary,
            RiskLevel = env.RiskLevel,
            Policy = env.Policy,
            CreatedAt = createdAt,
            DeadlineAt = env.TimeoutAt,
            AnswerMessageId = answerMessageId,
        };
    }

    private readonly record struct AgentDecisionRow(Guid Id, DateTimeOffset CreatedDate, Guid? ApprovalMessageId, string? DecisionEnvelopeJson);

    private readonly record struct NodeDecisionRow(Guid Id, DateTimeOffset CreatedAt, string? PayloadJson);
}
