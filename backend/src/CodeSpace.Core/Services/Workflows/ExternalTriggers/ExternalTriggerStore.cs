using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

public sealed class ExternalTriggerStore(CodeSpaceDbContext db, ExternalTriggerKeySealer sealer) : IExternalTriggerStore
{
    public async Task<IReadOnlyList<ExternalTriggerView>> ListForWorkflowAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken) =>
        await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.WorkflowId == workflowId && t.TeamId == teamId && t.DeletedDate == null)
            .OrderBy(t => t.NodeId)
            .Select(t => new ExternalTriggerView(t.Id, t.TeamId, t.WorkflowId, t.NodeId, t.Slug))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<ExternalTriggerView?> FindAsync(string slug, Guid teamId, CancellationToken cancellationToken) =>
        await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.Slug == slug && t.TeamId == teamId && t.DeletedDate == null)
            .Select(t => new ExternalTriggerView(t.Id, t.TeamId, t.WorkflowId, t.NodeId, t.Slug))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The row owns the endpoint name and credential: a stale draft must not undo an explicit rename or rotation.
    /// Node identity survives activation replacement on every save; an activation id would retire a working credential.
    /// </summary>
    public async Task<ExternalTriggerView> EnsureForNodeAsync(Guid workflowId, Guid teamId, string nodeId, string? desiredSlug, Guid actorId, CancellationToken cancellationToken)
    {
        var slug = DeriveRequiredSlug(desiredSlug, "slug");
        var row = await db.WorkflowExternalTrigger.SingleOrDefaultAsync(
            t => t.WorkflowId == workflowId && t.TeamId == teamId && t.NodeId == nodeId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false);
        if (row is not null) return View(row);

        if (!await db.Workflow.AnyAsync(w => w.Id == workflowId && w.TeamId == teamId && w.DeletedDate == null, cancellationToken).ConfigureAwait(false))
            throw new KeyNotFoundException("Workflow not found in this team.");

        var key = sealer.Mint();
        row = new WorkflowExternalTrigger
        {
            Id = Guid.NewGuid(), WorkflowId = workflowId, TeamId = teamId, NodeId = nodeId, Slug = slug,
            KeyDigest = key.Digest, KeyCiphertext = key.Ciphertext, CreatedBy = actorId, LastModifiedBy = actorId,
        };
        db.WorkflowExternalTrigger.Add(row);
        await SaveAsync(slug, cancellationToken).ConfigureAwait(false);
        return View(row);
    }

    public async Task<string> RotateKeyAsync(Guid triggerId, Guid teamId, Guid actorId, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(triggerId, teamId, cancellationToken).ConfigureAwait(false);
        var key = sealer.Mint();
        row.KeyDigest = key.Digest;
        row.KeyCiphertext = key.Ciphertext;
        row.LastModifiedBy = actorId;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return key.Key;
    }

    public async Task RenameAsync(Guid triggerId, Guid teamId, string desiredSlug, Guid actorId, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(triggerId, teamId, cancellationToken).ConfigureAwait(false);
        row.Slug = DeriveRequiredSlug(desiredSlug, nameof(desiredSlug));
        row.LastModifiedBy = actorId;
        await SaveAsync(row.Slug, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowExternalTrigger> LoadAsync(Guid triggerId, Guid teamId, CancellationToken cancellationToken) =>
        await db.WorkflowExternalTrigger.SingleOrDefaultAsync(t => t.Id == triggerId && t.TeamId == teamId && t.DeletedDate == null, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("External trigger not found in this team.");

    private async Task SaveAsync(string slug, CancellationToken cancellationToken)
    {
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "uq_wet_slug" })
        {
            throw new ExternalTriggerSlugConflictException(slug, ex);
        }
    }

    private static string DeriveRequiredSlug(string? desiredSlug, string parameterName)
    {
        var slug = ExternalTriggerSlug.Derive(desiredSlug);
        if (!ExternalTriggerSlug.IsUsable(slug))
            throw new ArgumentException("The name you entered must derive into a non-empty external trigger slug. Include at least one ASCII letter, digit or underscore. Hyphens and underscores are allowed in the resulting slug.", parameterName);
        return slug;
    }

    private static ExternalTriggerView View(WorkflowExternalTrigger row) => new(row.Id, row.TeamId, row.WorkflowId, row.NodeId, row.Slug);
}
