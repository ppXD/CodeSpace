using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Publish;

/// <summary>
/// The publish-or-park ledger's read/write surface — the single place that upserts a <see cref="PublishManifest"/>
/// row, so every writer (the live path, an S6 revise round, the re-attach path) and every reader (dependent-subtask
/// staging, the supervisor decider, the session room, cross-turn fold) goes through the SAME idempotent shape.
///
/// <para>Update-first: within one agent run's lifetime the SAME (AgentRunId, RepositoryAlias) key is written
/// repeatedly (once per S6 revise round, re-verifying the same subtask) — the common case is "the row already
/// exists, refresh it," not "insert a new one." A genuinely first write falls through to an INSERT, and a racing
/// duplicate INSERT (two writers observing the same run concurrently — e.g. a stale live worker vs. a reattach) loses
/// to the unique index and is folded into a plain UPDATE, mirroring <c>ToolCallLedgerService.TryClaimAsync</c>'s
/// insert-race recovery. Either path leaves exactly one row per key — never a duplicate branch record.</para>
/// </summary>
public interface IPublishManifestStore
{
    /// <summary>Upsert the <see cref="PublishManifestKind.Agent"/> row for one agent run's one repository.</summary>
    Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, CancellationToken cancellationToken);

    /// <summary>Upsert the <see cref="PublishManifestKind.Integration"/> row for one workflow run's one repository (no owning agent run).</summary>
    Task UpsertForIntegrationAsync(PublishManifestUpsert input, CancellationToken cancellationToken);

    /// <summary>Every manifest row for one agent run (one per writable repository), team-scoped.</summary>
    Task<IReadOnlyList<PublishManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Every manifest row (agent + integration) for one workflow run, newest first, team-scoped — the room / decider / session-fold read path.</summary>
    Task<IReadOnlyList<PublishManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// The bulk sibling of <see cref="ListForWorkflowRunAsync"/> — every manifest row for MANY workflow runs in one
    /// query, grouped by <c>WorkflowRunId</c>, team-scoped. For a session's turn-by-turn fold / continuity scan,
    /// which needs "does THIS run have a manifest" for every run it's already loaded — never one query per run.
    /// A run with no manifest rows is simply absent from the result (never an empty-list entry).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>> ListForWorkflowRunsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken);
}

public sealed class PublishManifestStore : IPublishManifestStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public PublishManifestStore(CodeSpaceDbContext db) { _db = db; }

    public Task UpsertForAgentRunAsync(Guid agentRunId, PublishManifestUpsert input, CancellationToken cancellationToken) =>
        UpsertAsync(PublishManifestKind.Agent, agentRunId: agentRunId, input, cancellationToken);

    public Task UpsertForIntegrationAsync(PublishManifestUpsert input, CancellationToken cancellationToken) =>
        UpsertAsync(PublishManifestKind.Integration, agentRunId: null, input, cancellationToken);

    /// <summary>
    /// Never add <c>CreatedBy</c>/<c>CreatedBy</c>-derived fields to either <c>ExecuteUpdateAsync</c>'s
    /// <c>SetProperty</c> list below. Both are bulk SQL updates that bypass <c>SaveChangesAsync</c>/
    /// <c>CodeSpaceDbContext.ApplyAuditFields</c> entirely — <c>CreatedBy</c> currently survives an update ONLY
    /// because it is absent from the list, which is exactly what <c>IHumanTouchReader.RoomOpenedPullRequestCountsAsync</c>
    /// relies on to tell a human-initiated PR-open from a system-authored one. Adding it here would silently let a
    /// later system write reassign a row's actor and misclassify a genuine human touch.
    /// </summary>
    private async Task UpsertAsync(PublishManifestKind kind, Guid? agentRunId, PublishManifestUpsert input, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var updated = await _db.PublishManifest
            .Where(m => m.Kind == kind && m.AgentRunId == agentRunId && m.WorkflowRunId == input.WorkflowRunId && m.RepositoryAlias == input.RepositoryAlias)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.RepositoryId, input.RepositoryId)
                .SetProperty(m => m.BaseSha, input.BaseSha)
                .SetProperty(m => m.Branch, input.Branch)
                .SetProperty(m => m.CommitSha, input.CommitSha)
                .SetProperty(m => m.PatchArtifactId, input.PatchArtifactId)
                .SetProperty(m => m.ChangedFileCount, input.ChangedFileCount)
                .SetProperty(m => m.ChangedFilesJson, input.ChangedFilesJson)
                .SetProperty(m => m.AcceptanceState, input.AcceptanceState)
                .SetProperty(m => m.PublishStateValue, input.PublishStateValue)
                .SetProperty(m => m.PublishError, input.PublishError)
                .SetProperty(m => m.Summary, input.Summary)
                .SetProperty(m => m.PullRequestNumber, input.PullRequestNumber)
                .SetProperty(m => m.PullRequestUrl, input.PullRequestUrl)
                .SetProperty(m => m.LastModifiedDate, now), cancellationToken)
            .ConfigureAwait(false);

        if (updated > 0) return;

        var row = new PublishManifest
        {
            Id = Guid.NewGuid(),
            TeamId = input.TeamId,
            Kind = kind,
            WorkflowRunId = input.WorkflowRunId,
            AgentRunId = agentRunId,
            RepositoryId = input.RepositoryId,
            RepositoryAlias = input.RepositoryAlias,
            BaseSha = input.BaseSha,
            Branch = input.Branch,
            CommitSha = input.CommitSha,
            PatchArtifactId = input.PatchArtifactId,
            ChangedFileCount = input.ChangedFileCount,
            ChangedFilesJson = input.ChangedFilesJson,
            AcceptanceState = input.AcceptanceState,
            PublishStateValue = input.PublishStateValue,
            PublishError = input.PublishError,
            Summary = input.Summary,
            PullRequestNumber = input.PullRequestNumber,
            PullRequestUrl = input.PullRequestUrl,
        };

        _db.PublishManifest.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the insert race to a concurrent writer (a stale live worker vs. a reattach observing the same
            // run) — fold into the same UPDATE the common path takes, so this call still leaves the row refreshed.
            _db.ChangeTracker.Clear();

            await _db.PublishManifest
                .Where(m => m.Kind == kind && m.AgentRunId == agentRunId && m.WorkflowRunId == input.WorkflowRunId && m.RepositoryAlias == input.RepositoryAlias)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.RepositoryId, input.RepositoryId)
                    .SetProperty(m => m.BaseSha, input.BaseSha)
                    .SetProperty(m => m.Branch, input.Branch)
                    .SetProperty(m => m.CommitSha, input.CommitSha)
                    .SetProperty(m => m.PatchArtifactId, input.PatchArtifactId)
                    .SetProperty(m => m.ChangedFileCount, input.ChangedFileCount)
                    .SetProperty(m => m.ChangedFilesJson, input.ChangedFilesJson)
                    .SetProperty(m => m.AcceptanceState, input.AcceptanceState)
                    .SetProperty(m => m.PublishStateValue, input.PublishStateValue)
                    .SetProperty(m => m.PublishError, input.PublishError)
                    .SetProperty(m => m.Summary, input.Summary)
                    .SetProperty(m => m.PullRequestNumber, input.PullRequestNumber)
                    .SetProperty(m => m.PullRequestUrl, input.PullRequestUrl)
                    .SetProperty(m => m.LastModifiedDate, now), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<PublishManifest>> ListForAgentRunAsync(Guid agentRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.PublishManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId && m.TeamId == teamId)
            .OrderBy(m => m.RepositoryAlias)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PublishManifest>> ListForWorkflowRunAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.PublishManifest.AsNoTracking()
            .Where(m => m.WorkflowRunId == workflowRunId && m.TeamId == teamId)
            .OrderByDescending(m => m.CreatedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>> ListForWorkflowRunsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (workflowRunIds.Count == 0) return EmptyByRun;

        var rows = await _db.PublishManifest.AsNoTracking()
            .Where(m => m.WorkflowRunId != null && workflowRunIds.Contains(m.WorkflowRunId.Value) && m.TeamId == teamId)
            .OrderByDescending(m => m.CreatedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.GroupBy(m => m.WorkflowRunId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<PublishManifest>)g.ToList());
    }

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>> EmptyByRun = new Dictionary<Guid, IReadOnlyList<PublishManifest>>();

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
