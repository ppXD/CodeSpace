using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public sealed class ExternalTriggerStore(CodeSpaceDbContext db, ExternalTriggerKeySealer sealer) : IExternalTriggerStore
{
    public async Task<IReadOnlyList<ExternalTriggerView>> ListForWorkflowAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken) =>
        await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.WorkflowId == workflowId && t.TeamId == teamId && t.DeletedDate == null)
            .OrderBy(t => t.NodeId)
            .Select(t => new ExternalTriggerView(t.Id, t.TeamId, t.WorkflowId, t.NodeId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ExternalTriggerView?> FindAsync(Guid triggerId, Guid teamId, CancellationToken cancellationToken) =>
        await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.Id == triggerId && t.TeamId == teamId && t.DeletedDate == null)
            .Select(t => new ExternalTriggerView(t.Id, t.TeamId, t.WorkflowId, t.NodeId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Node identity survives activation replacement on every save; an activation id would retire a working credential.
    /// </summary>
    public async Task<ExternalTriggerView> EnsureForNodeAsync(Guid workflowId, Guid teamId, string nodeId, Guid actorId, CancellationToken cancellationToken)
    {
        var row = await db.WorkflowExternalTrigger.SingleOrDefaultAsync(
            t => t.WorkflowId == workflowId && t.TeamId == teamId && t.NodeId == nodeId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false);
        if (row is not null) return View(row);

        if (!await db.Workflow.AnyAsync(w => w.Id == workflowId && w.TeamId == teamId && w.DeletedDate == null, cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("Workflow not found in this team.");

        var key = sealer.Mint();
        row = new WorkflowExternalTrigger
        {
            Id = Guid.NewGuid(), WorkflowId = workflowId, TeamId = teamId, NodeId = nodeId,
            KeyDigest = key.Digest, KeyCiphertext = key.Ciphertext, CreatedBy = actorId, LastModifiedBy = actorId,
        };
        db.WorkflowExternalTrigger.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return View(row);
    }

    public async Task<string> RotateKeyAsync(Guid triggerId, Guid teamId, Guid actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await ScopedTransaction.OwnOrJoinAsync(db.Database, cancellationToken).ConfigureAwait(false);
        var workflowId = await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.Id == triggerId && t.TeamId == teamId)
            .Select(t => (Guid?)t.WorkflowId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("External trigger not found in this team.");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM workflow WHERE id = {workflowId} AND team_id = {teamId} FOR UPDATE", cancellationToken).ConfigureAwait(false);
        var row = await db.WorkflowExternalTrigger.FromSqlInterpolated(
                $"SELECT * FROM workflow_external_trigger WHERE id = {triggerId} AND team_id = {teamId} AND deleted_date IS NULL FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("External trigger not found in this team.");
        var key = sealer.Mint();
        row.KeyDigest = key.Digest;
        row.KeyCiphertext = key.Ciphertext;
        row.LastModifiedBy = actorId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return key.Key;
    }

    private static ExternalTriggerView View(WorkflowExternalTrigger row) => new(row.Id, row.TeamId, row.WorkflowId, row.NodeId);
}
