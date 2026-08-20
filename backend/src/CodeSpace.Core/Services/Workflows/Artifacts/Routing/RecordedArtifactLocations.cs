using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// The read half of the routing seam, shared by every routed data class: which storage-profile revisions an object is
/// DURABLY RECORDED under. A read resolves through this and never through <see cref="IRoutedDestinationResolver"/>,
/// which is the property that keeps a class's history readable after its route is repointed, disabled or retired.
///
/// <para>Only <c>Available</c> locations qualify — a Pending, Missing, Corrupt or Deleted observation is not something
/// to open. Nothing here is data-class specific: the key is the object id, so a new data class reuses this verbatim
/// and the only thing it owns is how its own rows reach an object id.</para>
///
/// <para><b>Ordering is projected, not applied.</b> A caller joining this onto its own rows must order by
/// <see cref="RecordedArtifactLocation.VerifiedAt"/> descending then <see cref="RecordedArtifactLocation.LocationId"/>
/// to get freshest-observation-first, because a SQL join does not preserve the order of its operand. The keys are in
/// the projection so that ordering is available wherever the join happens.</para>
/// </summary>
public static class RecordedArtifactLocations
{
    /// <summary>Every Available location for one team, one row per (object, profile revision). Compose a <c>Where</c> on the object id, or join it onto rows that carry one.</summary>
    public static IQueryable<RecordedArtifactLocation> AvailableFor(CodeSpaceDbContext db, Guid teamId) =>
        from location in db.ArtifactLocation.AsNoTracking()
        join revision in db.StorageProfileRevision.AsNoTracking()
            on new { location.TeamId, Id = location.StorageProfileRevisionId } equals new { revision.TeamId, revision.Id }
        where location.TeamId == teamId && location.State == ArtifactLocationState.Available
        select new RecordedArtifactLocation
        {
            ArtifactObjectId = location.ArtifactObjectId, StorageProfileId = revision.StorageProfileId,
            StorageProfileRevision = revision.Revision, VerifiedAt = location.VerifiedAt, LocationId = location.Id,
        };
}

/// <summary>
/// One durable observation of one object under one immutable profile revision. Lives beside its query rather than in
/// <c>CodeSpace.Messages</c>, matching the routing vocabulary it belongs to (<c>StorageRouteSnapshot</c>,
/// <c>RoutedDestination</c>) — splitting one seam's types across two assemblies would cost more than it buys.
///
/// <para>Assigned member-by-member rather than through a positional constructor on purpose: EF Core cannot map a
/// property of a constructor-built projection back to its source column, so composing a <c>Where</c> or
/// <c>OrderBy</c> onto <see cref="RecordedArtifactLocations.AvailableFor"/> would fail to translate — which is the
/// whole point of exposing it as a composable query.</para>
/// </summary>
public sealed record RecordedArtifactLocation
{
    public required Guid ArtifactObjectId { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required int StorageProfileRevision { get; init; }
    public required DateTimeOffset? VerifiedAt { get; init; }
    public required Guid LocationId { get; init; }
}
