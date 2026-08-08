using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// P2 (v4.3, first slice): the ONE way a completion-ledger writer advances the run's monotonic
/// <c>CompletionLedgerVersion</c> — an atomic SQL increment (the value is computed inside the UPDATE, never
/// read-modify-written, so concurrent writers can never lose a bump). Callers are the writers whose writes a
/// watermark COUNT cannot see: the contract store's requirement amendment (row overwritten in place) and the
/// publish-manifest upsert (an ExecuteUpdate state transition on the same row). Insert-shaped writes are already
/// visible to the counts; bumping there too costs only a harmless extra recompose, so writers bump on ANY
/// successful write rather than distinguishing. A run id with no row (a unit-tier context, a benchmark's bare
/// agent run) updates zero rows — a no-op, never a fault.
/// </summary>
public static class CompletionLedgerVersionBump
{
    public static Task BumpAsync(CodeSpaceDbContext db, Guid workflowRunId, CancellationToken cancellationToken) =>
        db.WorkflowRun.Where(r => r.Id == workflowRunId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CompletionLedgerVersion, r => r.CompletionLedgerVersion + 1), cancellationToken);
}
