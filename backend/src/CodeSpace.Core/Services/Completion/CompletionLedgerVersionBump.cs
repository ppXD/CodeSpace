using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// P2 (v4.3, first slice): the ONE way a completion-ledger writer advances the run's monotonic ledger version —
/// an atomic upsert-increment on <c>completion_ledger_head</c> (the value is computed inside the statement, so
/// concurrent writers can never lose a bump). A SIDE table on purpose: the workflow_run row carries an xmin
/// concurrency token and is tracked by the engine for the whole turn, so a side-writer's UPDATE there aborts the
/// engine's own save (proven live, run 31230952188). Callers are the writers whose writes a watermark COUNT
/// cannot see: the contract store's requirement amendment (row overwritten in place) and the publish-manifest
/// upsert (an ExecuteUpdate state transition). Insert-shaped writes bump too — a harmless extra recompose, never
/// a missed one.
/// </summary>
public static class CompletionLedgerVersionBump
{
    public static Task BumpAsync(CodeSpaceDbContext db, Guid workflowRunId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO completion_ledger_head (workflow_run_id, version) VALUES ({workflowRunId}, 1) ON CONFLICT (workflow_run_id) DO UPDATE SET version = completion_ledger_head.version + 1", cancellationToken);
}
