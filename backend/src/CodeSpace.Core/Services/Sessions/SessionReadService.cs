using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Sessions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionReadService"/>. Reads the work-session thread model into the conversation DTOs over the
/// shared request-scoped <see cref="CodeSpaceDbContext"/>. All grouping / latest-wins logic lives in the pure
/// <see cref="SessionProjection"/>; this service only loads the bounded row sets + the pending-decision set and
/// delegates. Tenancy: every query filters <c>TeamId</c>, so a foreign session / run reads as not-found (null) — never
/// a leak. Nested sub-workflow children (<c>SourceType == workflow.child</c>) are excluded everywhere — they live inside
/// their parent turn, not as their own turn / attempt.
/// </summary>
public sealed class SessionReadService : ISessionReadService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;

    /// <summary>Clamp the page size — a sidebar page is small; a caller can't force an unbounded scan.</summary>
    internal const int MaxPageSize = 100;

    public SessionReadService(CodeSpaceDbContext db, IPublishManifestStore manifests)
    {
        _db = db;
        _manifests = manifests;
    }

    public async Task<SessionPage> ListAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var keyset = SessionCursor.Decode(cursor);
        var take = Math.Clamp(limit, 1, MaxPageSize);

        var query = _db.WorkSession.AsNoTracking().Where(s => s.TeamId == teamId);

        // Keyset: "strictly after" the cursor under (last_activity_at DESC, id DESC) — the tuple < comparison. Id is
        // compared in Postgres uuid order (Id.CompareTo translates to it), the SAME order the index + ORDER BY use.
        if (keyset is { } c)
            query = query.Where(s => s.LastActivityAt < c.LastActivityAt || (s.LastActivityAt == c.LastActivityAt && s.Id.CompareTo(c.Id) < 0));

        // Fetch one extra row to know whether a further page exists without a second COUNT query.
        var rows = await query
            .OrderByDescending(s => s.LastActivityAt).ThenByDescending(s => s.Id)
            .Take(take + 1)
            .Select(s => new { s.Id, s.Title, s.Kind, s.Status, s.LastTurnIndex, s.CreatedDate, s.LastActivityAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > take;
        var page = hasMore ? rows.GetRange(0, take) : rows;
        var sessionIds = page.Select(s => s.Id).ToList();

        // The page's runs (bounded by the page) — for the latest-run badge + the per-session pending-decision flag. One
        // query for the run rows, then the pending set; both scoped to this page's sessions (no per-row subquery).
        var runRows = sessionIds.Count == 0
            ? new List<SessionProjection.SessionRunRow>()
            : (await _db.WorkflowRun.AsNoTracking()
                .Where(r => r.TeamId == teamId && r.SessionId != null && sessionIds.Contains(r.SessionId.Value) && r.SourceType != WorkflowRunSourceTypes.ChildWorkflow)
                .Select(r => new { SessionId = r.SessionId!.Value, r.Id, r.Status, r.ProjectionKind, r.CreatedDate, r.RootRunId, r.SessionTurnIndex })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(r => new SessionProjection.SessionRunRow(r.SessionId, r.Id, r.Status, r.ProjectionKind, r.CreatedDate, r.RootRunId, r.SessionTurnIndex))
                .ToList();

        var latestBySession = SessionProjection.LatestRunBySession(runRows);

        var pendingRunIds = await LoadPendingDecisionRunIdsAsync(runRows.Select(r => r.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var pendingSessions = runRows.Where(r => pendingRunIds.Contains(r.Id)).Select(r => r.SessionId).ToHashSet();

        var items = page.Select(s =>
        {
            var latest = latestBySession.GetValueOrDefault(s.Id);
            return new SessionSummary
            {
                Id = s.Id,
                Title = s.Title,
                Kind = s.Kind,
                Status = s.Status,
                TurnCount = s.LastTurnIndex,
                CreatedDate = s.CreatedDate,
                LastActivityAt = s.LastActivityAt,
                LatestRunId = latest?.Id,
                LatestRunStatus = latest?.Status,
                LatestProjectionKind = latest?.ProjectionKind,
                HasPendingDecision = pendingSessions.Contains(s.Id),
            };
        }).ToList();

        var nextCursor = hasMore ? new SessionCursor(page[^1].LastActivityAt, page[^1].Id).Encode() : null;

        return new SessionPage { Items = items, NextCursor = nextCursor };
    }

    public async Task<SessionDetail?> GetDetailAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        var session = await _db.WorkSession.AsNoTracking()
            .Where(s => s.Id == sessionId && s.TeamId == teamId)
            .Select(s => new { s.Id, s.Title, s.Kind, s.Status, s.CreatedDate, s.Summary, s.SummaryThroughTurnIndex })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (session == null) return null;   // foreign / missing — indistinguishable not-found

        var turns = await BuildTurnsAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        return new SessionDetail
        {
            Id = session.Id,
            Title = session.Title,
            Kind = session.Kind,
            Status = session.Status,
            CreatedDate = session.CreatedDate,
            Summary = session.Summary,
            SummaryThroughTurnIndex = session.SummaryThroughTurnIndex,
            AnchorTurnIndex = null,
            Turns = turns,
        };
    }

    public async Task<SessionDetail?> GetByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId && r.TeamId == teamId)
            .Select(r => new { r.SessionId, r.RootRunId, r.Id })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (run?.SessionId is not { } sessionId) return null;   // foreign / missing / session-less — not-found

        var detail = await GetDetailAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);
        if (detail == null) return null;

        // Anchor at the turn this run belongs to: its lineage root is the turn run's id (an attempt's RootRunId == the
        // turn run's id; a turn run is its own root). Match it among the built turns; null if the run isn't a turn here.
        var rootKey = run.RootRunId ?? run.Id;
        var anchor = detail.Turns.FirstOrDefault(t => t.TurnRunId == rootKey)?.TurnIndex;

        return detail with { AnchorTurnIndex = anchor };
    }

    /// <summary>
    /// The session's runs (turns + their rerun attempts), excluding nested sub-workflow children — one query joining the
    /// request for the goal payload, then the pure projection groups them into turns (latest-wins) + nests attempts. The
    /// pending-decision set is over the loaded runs (the turns surface it on their latest attempt).
    /// </summary>
    private async Task<IReadOnlyList<SessionTurn>> BuildTurnsAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        var raw = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.TeamId == teamId && r.SourceType != WorkflowRunSourceTypes.ChildWorkflow)
            .Select(r => new
            {
                r.Id, r.RootRunId, r.SessionTurnIndex, r.Status, r.ProjectionKind, r.SourceType, r.RerunFromNodeId,
                r.CreatedDate, r.StartedAt, r.CompletedAt, r.Error, r.OutputsJson,
                Goal = r.RunRequest.NormalizedPayloadJson, r.ScopeRepositoryIds,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (raw.Count == 0) return Array.Empty<SessionTurn>();

        var rows = raw.Select(r => new SessionProjection.RunRow(
            r.Id, r.RootRunId, r.SessionTurnIndex, r.Status, r.ProjectionKind, r.SourceType, r.RerunFromNodeId,
            r.CreatedDate, r.StartedAt, r.CompletedAt, r.Error, r.OutputsJson, r.Goal, r.ScopeRepositoryIds)).ToList();

        var runIds = rows.Select(r => r.Id).ToList();

        var pending = await LoadPendingDecisionRunIdsAsync(runIds, cancellationToken).ConfigureAwait(false);
        var manifestsByRunId = await _manifests.ListForWorkflowRunsAsync(runIds, teamId, cancellationToken).ConfigureAwait(false);

        return SessionProjection.BuildTurns(rows, pending, manifestsByRunId);
    }

    /// <summary>
    /// The subset of <paramref name="runIds"/> parked on a pending decision, on EITHER park backend — a node-grain
    /// <c>workflow_run_wait</c> in Decision/Pending, or an agent-grain <c>tool_call_ledger</c> decision.request awaiting
    /// approval (reached via the run's agent runs). Mirrors <c>WorkflowService.HasPendingDecisionPredicate</c> (the
    /// filter authority); kept as two scoped set queries here so the read projection needs no run-by-run subquery.
    /// </summary>
    private async Task<HashSet<Guid>> LoadPendingDecisionRunIdsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return new HashSet<Guid>();

        var waitPending = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => runIds.Contains(w.RunId) && w.WaitKind == WorkflowWaitKinds.Decision && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.RunId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ledgerPending = await _db.AgentRun.AsNoTracking()
            .Where(a => a.WorkflowRunId != null && runIds.Contains(a.WorkflowRunId.Value)
                && _db.ToolCallLedger.Any(t => t.AgentRunId == a.Id && t.ToolKind == DecisionToolKinds.DecisionRequest
                    && t.Status == ToolCallLedgerStatus.AwaitingApproval && t.ApprovedAt == null))
            .Select(a => a.WorkflowRunId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var pending = new HashSet<Guid>(waitPending);
        pending.UnionWith(ledgerPending);
        return pending;
    }
}
