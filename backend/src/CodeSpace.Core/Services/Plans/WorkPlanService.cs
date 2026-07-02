using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Plans;

/// <summary>
/// The Postgres <see cref="IWorkPlanService"/>. Insert-only versioning: the two unique indexes —
/// (run, version) and the partial (run, origin_key) — are the authority; a concurrent/replayed writer that
/// loses an insert race re-reads (origin-key hit ⇒ return the existing row) or re-numbers (version race ⇒
/// retry with the fresh max). No row is ever updated here, so a plan version is immutable once written.
/// </summary>
public sealed class WorkPlanService : IWorkPlanService, IScopedDependency
{
    /// <summary>Version-race retries before giving up — races need another writer on the SAME run in the same instant, so one loser retry is almost always enough; three bounds a pathological loop.</summary>
    private const int MaxInsertRetries = 3;

    private readonly CodeSpaceDbContext _db;

    public WorkPlanService(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<WorkPlan> SaveVersionAsync(WorkPlanDraft draft, CancellationToken cancellationToken)
    {
        if (await FindByOriginKeyAsync(draft, cancellationToken).ConfigureAwait(false) is { } existing) return existing;

        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await InsertNextVersionAsync(draft, cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < MaxInsertRetries)
                {
                    DetachPendingInserts();

                    // An origin-key collision means a replayed twin won the race — its row IS this save's result.
                    // A version collision means an unrelated concurrent version — loop and re-number.
                    if (await FindByOriginKeyAsync(draft, cancellationToken).ConfigureAwait(false) is { } raced) return raced;
                }
            }
        }
        finally
        {
            // EVERY failure exit (retry exhaustion, FK violation, cancellation) must leave the SHARED scoped
            // context clean — a doomed Added entity left tracked would poison the caller's next SaveChanges.
            DetachPendingInserts();
        }
    }

    public async Task<WorkPlan?> GetCurrentAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkPlan.AsNoTracking().Where(p => p.WorkflowRunId == workflowRunId && p.TeamId == teamId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<WorkPlan>> ListVersionsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkPlan.AsNoTracking().Where(p => p.WorkflowRunId == workflowRunId && p.TeamId == teamId).OrderBy(p => p.Version).ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<WorkPlan?> FindByOriginKeyAsync(WorkPlanDraft draft, CancellationToken cancellationToken)
    {
        if (draft.OriginKey is null) return null;

        return await _db.WorkPlan.AsNoTracking()
            .FirstOrDefaultAsync(p => p.WorkflowRunId == draft.WorkflowRunId && p.TeamId == draft.TeamId && p.OriginKey == draft.OriginKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkPlan> InsertNextVersionAsync(WorkPlanDraft draft, CancellationToken cancellationToken)
    {
        var maxVersion = await _db.WorkPlan.Where(p => p.WorkflowRunId == draft.WorkflowRunId).MaxAsync(p => (int?)p.Version, cancellationToken).ConfigureAwait(false) ?? 0;

        var row = new WorkPlan
        {
            Id = Guid.NewGuid(),
            TeamId = draft.TeamId,
            WorkflowRunId = draft.WorkflowRunId,
            Version = maxVersion + 1,
            Status = WorkPlanStatuses.Authored,
            OriginKind = draft.OriginKind,
            OriginKey = draft.OriginKey,
            Goal = draft.Goal,
            ItemsJson = JsonSerializer.Serialize(draft.Items, AgentJson.Options),
            SuccessCriteriaJson = draft.SuccessCriteria is { Count: > 0 } criteria ? JsonSerializer.Serialize(criteria, AgentJson.Options) : null,
            RisksJson = draft.Risks is { Count: > 0 } risks ? JsonSerializer.Serialize(risks, AgentJson.Options) : null,
        };

        _db.WorkPlan.Add(row);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row;
    }

    /// <summary>Drop the failed pending insert from the change tracker so the retry starts clean (the scoped context lives on past this save).</summary>
    private void DetachPendingInserts()
    {
        foreach (var entry in _db.ChangeTracker.Entries<WorkPlan>().Where(e => e.State == EntityState.Added).ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>Only THIS table's unique indexes count as a retryable race — an unrelated entity's unique violation flushed through the shared scoped context must propagate, not be retried as a version race. Absent constraint metadata degrades to retry (bounded + fail-loud either way).</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && (pg.ConstraintName?.Contains("work_plan") ?? true);
}
