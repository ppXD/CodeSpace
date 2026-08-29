using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed class ProfileAbandonmentService : IProfileAbandonmentService
{
    private const int MaxBatchSize = 200;

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactCasPurgeCoordinator _purge;

    public ProfileAbandonmentService(CodeSpaceDbContext db, IArtifactCasPurgeCoordinator purge)
    {
        _db = db;
        _purge = purge;
    }

    public async Task<ProfileAbandonmentSummary> AbandonAsync(Guid teamId, Guid actorId, Guid profileId, int batchSize, CancellationToken cancellationToken)
    {
        var revisions = await RevisionIdsAsync(teamId, profileId, cancellationToken).ConfigureAwait(false);
        var batch = await UnreleasedAsync(teamId, revisions, Math.Clamp(batchSize, 1, MaxBatchSize), cancellationToken).ConfigureAwait(false);
        var abandoned = 0;
        var stillServed = 0;
        var unanswered = 0;

        foreach (var placement in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await AbandonOneAsync(teamId, actorId, placement, cancellationToken).ConfigureAwait(false))
            {
                case ArtifactCasAbandonResult.Abandoned: abandoned++; break;
                case ArtifactCasAbandonResult.StillServed: stillServed++; break;
                default: unanswered++; break;
            }
        }

        return new ProfileAbandonmentSummary
        {
            Examined = batch.Count, Abandoned = abandoned, StillServed = stillServed, Unanswered = unanswered,
            Remaining = await CountUnreleasedAsync(teamId, revisions, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Claims one placement and asks the destination about it.
    ///
    /// <para>A claim that cannot be taken is not a failure of the pass: the placement is being worked on by something
    /// else, or has already been settled. Either way the honest count is "no answer", and the next call sees it.</para>
    /// </summary>
    private async Task<ArtifactCasAbandonResult> AbandonOneAsync(Guid teamId, Guid actorId, Placement placement, CancellationToken cancellationToken)
    {
        var claimed = await _purge.ClaimAsync(new ArtifactCasPurgeRequest
        {
            TeamId = teamId, ArtifactObjectId = placement.ArtifactObjectId, ActorId = actorId, ArtifactLocationId = placement.LocationId,
        }, cancellationToken).ConfigureAwait(false);

        return claimed is ArtifactCasPurgeClaimResult.Claimed claim
            ? await _purge.AbandonAsync(claim.Claim, cancellationToken).ConfigureAwait(false)
            : new ArtifactCasAbandonResult.Rejected(new ArtifactCasProblem(ArtifactCasProblemCode.LocationUnavailable, true));
    }

    private async Task<List<Guid>> RevisionIdsAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken) =>
        await _db.StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.TeamId == teamId && revision.StorageProfileId == profileId)
            .Select(revision => revision.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<List<Placement>> UnreleasedAsync(Guid teamId, List<Guid> revisions, int take, CancellationToken cancellationToken) =>
        revisions.Count == 0 ? [] : await Unreleased(teamId, revisions)
            .OrderBy(location => location.Id)
            .Take(take)
            .Select(location => new Placement(location.Id, location.ArtifactObjectId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<int> CountUnreleasedAsync(Guid teamId, List<Guid> revisions, CancellationToken cancellationToken) =>
        revisions.Count == 0 ? 0 : await Unreleased(teamId, revisions).CountAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The same population the retirement guard counts, so a caller draining to zero is draining to what actually unblocks them.</summary>
    private IQueryable<ArtifactLocation> Unreleased(Guid teamId, List<Guid> revisions) =>
        _db.ArtifactLocation.AsNoTracking().Where(location => location.TeamId == teamId
            && revisions.Contains(location.StorageProfileRevisionId)
            && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted);

    private sealed record Placement(Guid LocationId, Guid ArtifactObjectId);
}
