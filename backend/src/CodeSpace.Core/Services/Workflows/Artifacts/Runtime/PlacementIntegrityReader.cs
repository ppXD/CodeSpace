using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// One grouped pass over the team's placements.
///
/// <para>Every predicate here rides <c>ix_artifact_location_state_verified (team_id, state, verified_at, id)</c>, which
/// is why this is a team-level question rather than a per-profile one: attributing counts to a profile means joining
/// through <c>storage_profile_revision</c>, which that index cannot serve, for an answer an operator would read as one
/// number anyway.</para>
/// </summary>
public sealed class PlacementIntegrityReader : IPlacementIntegrityReader
{
    private readonly CodeSpaceDbContext _db;

    public PlacementIntegrityReader(CodeSpaceDbContext db) => _db = db;

    public async Task<PlacementIntegritySummary> ReadAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var counted = await _db.ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == teamId)
            .GroupBy(location => location.State)
            .Select(grouped => new StateCount(grouped.Key, grouped.Count()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PlacementIntegritySummary
        {
            Missing = CountOf(counted, ArtifactLocationState.Missing),
            Corrupt = CountOf(counted, ArtifactLocationState.Corrupt),
            Available = CountOf(counted, ArtifactLocationState.Available),
            OldestVerifiedAt = await OldestVerifiedAtAsync(teamId, cancellationToken).ConfigureAwait(false),
        };
    }

    private static int CountOf(IEnumerable<StateCount> rows, ArtifactLocationState state) =>
        rows.SingleOrDefault(row => row.State == state)?.Count ?? 0;

    private async Task<DateTimeOffset?> OldestVerifiedAtAsync(Guid teamId, CancellationToken cancellationToken) =>
        await Available(teamId).MinAsync(location => location.VerifiedAt, cancellationToken).ConfigureAwait(false);

    private IQueryable<ArtifactLocation> Available(Guid teamId) => _db.ArtifactLocation.AsNoTracking()
        .Where(location => location.TeamId == teamId && location.State == ArtifactLocationState.Available);

    private sealed record StateCount(ArtifactLocationState State, int Count);
}
