using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Reads a profile's placements across EVERY revision it has ever had.
///
/// <para>Not the current revision: a placement's <c>storage_profile_revision_id</c> is immutable for the row's life,
/// so a profile that was ever re-pointed holds rows under several revisions and a query keyed on the current one
/// reports a fraction of what is really there. Under-reporting here is the dangerous direction — it is the number an
/// operator decides an irreversible retirement on.</para>
/// </summary>
public sealed class ProfilePlacementReader : IProfilePlacementReader
{
    private readonly CodeSpaceDbContext _db;

    public ProfilePlacementReader(CodeSpaceDbContext db) => _db = db;

    public async Task<ProfilePlacementPage> ListAsync(Guid teamId, Guid profileId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit, 1, StoragePageLimits.MaxPageSize);
        var revisions = await RevisionsAsync(teamId, profileId, cancellationToken).ConfigureAwait(false);

        if (revisions.Count == 0) return new ProfilePlacementPage { Items = [] };

        var ids = revisions.Keys.ToList();
        var query = _db.ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == teamId && ids.Contains(location.StorageProfileRevisionId));

        if (Cursor(cursor) is { } after) query = query.Where(location => location.Id > after);

        var rows = await query.OrderBy(location => location.Id).Take(take + 1)
            .Select(location => new PlacementRow(location.Id, location.ArtifactObjectId, location.State, location.ObjectKey,
                location.StorageProfileRevisionId, location.ObservedSizeBytes, location.VerifiedAt, location.LastErrorCode))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.GetRange(0, take) : rows;

        return new ProfilePlacementPage
        {
            Items = page.Select(row => Summary(row, revisions[row.StorageProfileRevisionId])).ToList(),
            NextCursor = hasMore ? page[^1].LocationId.ToString("N") : null,
        };
    }

    public async Task<IReadOnlyList<ProfilePlacementTotal>> TotalsAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken)
    {
        var ids = (await RevisionsAsync(teamId, profileId, cancellationToken).ConfigureAwait(false)).Keys.ToList();

        if (ids.Count == 0) return [];

        var counted = await _db.ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == teamId && ids.Contains(location.StorageProfileRevisionId))
            .GroupBy(location => location.State)
            .Select(grouped => new { State = grouped.Key, Count = grouped.Count(), SizeBytes = grouped.Sum(location => location.ObservedSizeBytes ?? 0) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return counted
            .Select(row => new ProfilePlacementTotal { State = (ArtifactLocationStateValue)row.State, Count = row.Count, SizeBytes = row.SizeBytes })
            .OrderBy(row => row.State)
            .ToList();
    }

    /// <summary>Every revision the profile has ever had, by id. Small by construction, and the two-step keeps both queries on <c>ux_artifact_location_profile_object_key</c>'s leading column.</summary>
    private async Task<Dictionary<Guid, int>> RevisionsAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken) =>
        await _db.StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.TeamId == teamId && revision.StorageProfileId == profileId)
            .ToDictionaryAsync(revision => revision.Id, revision => revision.Revision, cancellationToken).ConfigureAwait(false);

    private static ProfilePlacementSummary Summary(PlacementRow row, int profileRevision) => new()
    {
        LocationId = row.LocationId,
        ArtifactObjectId = row.ArtifactObjectId,
        State = (ArtifactLocationStateValue)row.State,
        ObjectKey = row.ObjectKey,
        ProfileRevision = profileRevision,
        SizeBytes = row.SizeBytes,
        VerifiedAt = row.VerifiedAt,
        LastErrorCode = row.LastErrorCode,
    };

    private static Guid? Cursor(string? cursor) => Guid.TryParse(cursor, out var parsed) ? parsed : null;

    private sealed record PlacementRow(Guid LocationId, Guid ArtifactObjectId, ArtifactLocationState State, string ObjectKey,
        Guid StorageProfileRevisionId, long? SizeBytes, DateTimeOffset? VerifiedAt, string? LastErrorCode);
}
