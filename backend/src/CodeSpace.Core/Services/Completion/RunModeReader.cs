using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Completion;

/// <summary>
/// The ONE reading of a run's OPERATING MODE off its own row — the tasks lane's launch-stamped projection kind
/// wins; an authored run derives from its frozen definition (the snapshot json, else the workflow version's).
/// Shared by the terminal authority's mode gate and the shadow sweep's stage mirror so the two can never
/// disagree about which profile a run answers to.
/// </summary>
public static class RunModeReader
{
    public static async Task<string> DeriveAsync(Persistence.Db.CodeSpaceDbContext db, Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == workflowRunId && r.TeamId == teamId)
            .Select(r => new { r.ProjectionKind, r.DefinitionSnapshotJson, r.WorkflowId, r.WorkflowVersion })
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        var definitionJson = run.DefinitionSnapshotJson
            ?? await db.WorkflowVersion.AsNoTracking()
                .Where(v => v.WorkflowId == run.WorkflowId && v.Version == run.WorkflowVersion)
                .Select(v => v.DefinitionJson)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return RunModeClassifier.DeriveFromJson(run.ProjectionKind, definitionJson);
    }
}
