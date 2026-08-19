using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Messages.Artifacts;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The store's half of the retention ledger: minting a declaration atomically with the artifact insert, and revoking one
/// whenever a second producer takes the same id. Both live here rather than in the reaper because the store is the ONE
/// choke point every artifact write passes through, and that is what makes "no other producer holds this id" provable
/// instead of assumed.
/// </summary>
public sealed partial class ArtifactStore : IArtifactRetentionWriter
{
    public async Task<ArtifactRetentionWrite> PutDeclaredAsync(ArtifactRetentionWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ContentType))
            throw new ArgumentException("ContentType is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.HolderKind) || request.HolderId == Guid.Empty)
            throw new ArgumentException("A declaration must name the holder it is about to write.", nameof(request));

        return await WriteAsync(new ArtifactWrite(request.TeamId, request.Bytes, request.ContentType, request), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The declaration that rides the artifact's own INSERT. <see cref="ArtifactRetentionState.Declared"/> with
    /// <c>NextSweepAt</c> at the class's age floor: the reaper cannot even claim the row before the floor elapses, so a
    /// producer whose holder write is still in flight is not merely re-queued, it is never looked at.
    /// </summary>
    private static WorkflowArtifactRetention DeclarationFor(ArtifactRetentionWriteRequest request, WorkflowArtifact artifact)
    {
        var floor = ArtifactRetentionPolicy.For(request.RetentionClass)?.MinimumAge ?? ArtifactRetentionPolicy.MinimumAgeFloor;

        return new WorkflowArtifactRetention
        {
            ArtifactId = artifact.Id,
            TeamId = artifact.TeamId,
            RetentionClass = request.RetentionClass,
            HolderKind = request.HolderKind,
            HolderId = request.HolderId,
            State = ArtifactRetentionState.Declared,
            DeclaredAt = artifact.CreatedAt,
            NextSweepAt = artifact.CreatedAt.Add(floor),
            LastModifiedAt = artifact.CreatedAt,
        };
    }

    /// <summary>
    /// Kill any live declaration on <paramref name="artifactId"/>. Called from the dedup and race paths of the write:
    /// once two producers hold one id, this ledger can no longer claim to enumerate the artifact's references, and
    /// <see cref="ArtifactRetentionState.Revoked"/> is terminal so the artifact is kept for good.
    ///
    /// <para>Filtered to the LIVE states, so it matches no rows in the ordinary case and writes no tuple. It also
    /// serializes the write path against a collector: the collector holds this exact row under <c>FOR UPDATE</c> for its
    /// whole transaction, so this statement either commits first (and the collector reads <c>Revoked</c> and keeps the
    /// artifact) or waits for the collector (and then finds the row gone, which is what makes the caller's re-read of
    /// the artifact row necessary).</para>
    /// </summary>
    private async Task RevokeDeclarationAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE workflow_artifact_retention
            SET state = 'Revoked',
                terminal_at = clock_timestamp(),
                owner_id = NULL,
                lease_expires_at = NULL,
                last_error_code = 'declaration-revoked-by-later-writer',
                last_error_message = 'A later writer of the same content took this artifact id, so the declaration no longer enumerates every reference.',
                revision = revision + 1,
                last_modified_at = clock_timestamp()
            WHERE team_id = {teamId} AND artifact_id = {artifactId} AND state IN ('Declared', 'Quarantined')
            """, cancellationToken).ConfigureAwait(false);
}
