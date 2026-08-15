using System.Text.RegularExpressions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>
/// Reads the route identity, its exact current immutable policy revision, the team-owned active profile and the
/// selected immutable profile revision in one no-tracking database statement. It never invokes a broker/factory and
/// never selects provider configuration or credential material.
/// </summary>
public sealed class StorageRouteSnapshotResolver : IStorageRouteSnapshotResolver
{
    private readonly CodeSpaceDbContext _db;

    public StorageRouteSnapshotResolver(CodeSpaceDbContext db) => _db = db;

    public async Task<StorageRouteSnapshotResolution> ResolveAsync(StorageRouteSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (request == null || request.TeamId == Guid.Empty) return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.Request);
        if (!StorageRouteSnapshotProjection.IsValidTypeKey(request.DataClassTypeKey))
            return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.DataClassTypeKey);
        if (cancellationToken.IsCancellationRequested) return new StorageRouteSnapshotResolution.Cancelled();

        try
        {
            var row = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
            return StorageRouteSnapshotProjection.Resolve(row);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new StorageRouteSnapshotResolution.Cancelled();
        }
    }

    private Task<StorageRouteSnapshotRow?> ReadAsync(StorageRouteSnapshotRequest request, CancellationToken cancellationToken) =>
        (from route in _db.StorageRoute.AsNoTracking()
         join routeRevision in _db.StorageRouteRevision.AsNoTracking()
             on new { route.TeamId, StorageRouteId = route.Id, Revision = route.CurrentRevision }
             equals new { routeRevision.TeamId, routeRevision.StorageRouteId, routeRevision.Revision } into exactRouteRevisions
         from routeRevision in exactRouteRevisions.DefaultIfEmpty()
         join profile in _db.StorageProfile.AsNoTracking()
             on new { route.TeamId, StorageProfileId = routeRevision.StorageProfileId }
             equals new { profile.TeamId, StorageProfileId = profile.Id } into exactProfiles
         from profile in exactProfiles.DefaultIfEmpty()
         join profileRevision in _db.StorageProfileRevision.AsNoTracking()
             on new
             {
                 route.TeamId,
                 StorageProfileId = profile.Id,
                 Revision = routeRevision.ProfileRevisionMode == StorageProfileRevisionMode.CurrentAtWrite
                     ? profile.CurrentRevision
                     : routeRevision.ProfileRevisionMode == StorageProfileRevisionMode.Pinned
                         ? routeRevision.PinnedProfileRevision ?? 0
                         : 0,
             }
             equals new { profileRevision.TeamId, profileRevision.StorageProfileId, profileRevision.Revision } into exactProfileRevisions
         from profileRevision in exactProfileRevisions.DefaultIfEmpty()
         where route.TeamId == request.TeamId && route.DataClassTypeKey == request.DataClassTypeKey
         select new StorageRouteSnapshotRow
         {
             RouteId = route.Id,
             RouteRevision = route.CurrentRevision,
             DataClassTypeKey = route.DataClassTypeKey,
             RouteStateIsKnown = route.State == StorageRouteState.Draft || route.State == StorageRouteState.Active
                 || route.State == StorageRouteState.Disabled || route.State == StorageRouteState.Retired,
             RouteIsActive = route.State == StorageRouteState.Active,
             RouteRevisionExists = routeRevision != null,
             StorageProfileId = routeRevision == null ? Guid.Empty : routeRevision.StorageProfileId,
             ModeIsCurrentAtWrite = routeRevision != null && routeRevision.ProfileRevisionMode == StorageProfileRevisionMode.CurrentAtWrite,
             ModeIsPinned = routeRevision != null && routeRevision.ProfileRevisionMode == StorageProfileRevisionMode.Pinned,
             PinnedProfileRevision = routeRevision == null ? null : routeRevision.PinnedProfileRevision,
             ProfileExists = profile != null,
             ProfileStateIsKnown = profile != null && (profile.State == StorageProfileState.Draft || profile.State == StorageProfileState.Active
                 || profile.State == StorageProfileState.Disabled || profile.State == StorageProfileState.Retired),
             ProfileIsActive = profile != null && profile.State == StorageProfileState.Active,
             StorageProfileRevision = routeRevision == null || profile == null
                 ? 0
                 : routeRevision.ProfileRevisionMode == StorageProfileRevisionMode.CurrentAtWrite
                     ? profile.CurrentRevision
                     : routeRevision.ProfileRevisionMode == StorageProfileRevisionMode.Pinned
                         ? routeRevision.PinnedProfileRevision ?? 0
                         : 0,
             ProfileRevisionExists = profileRevision != null,
             ProviderTypeKey = profileRevision == null ? null : profileRevision.ProviderTypeKey,
             NamespaceFingerprint = profileRevision == null ? null : profileRevision.NamespaceFingerprint,
         })
        .SingleOrDefaultAsync(cancellationToken);
}

internal static partial class StorageRouteSnapshotProjection
{
    private const int MaxTypeKeyLength = 128;

    public static StorageRouteSnapshotResolution Resolve(StorageRouteSnapshotRow? row)
    {
        if (row == null) return new StorageRouteSnapshotResolution.Missing();
        if (!row.RouteStateIsKnown) return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.RouteState);
        if (!row.RouteIsActive) return new StorageRouteSnapshotResolution.RouteNotActive();
        if (!row.RouteRevisionExists) return new StorageRouteSnapshotResolution.RouteRevisionMissing();
        if (row.RouteId == Guid.Empty || row.RouteRevision <= 0 || !IsValidTypeKey(row.DataClassTypeKey) || row.StorageProfileId == Guid.Empty)
            return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.RouteRevision);
        if (!IsValidMode(row)) return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileRevisionMode);
        if (!row.ProfileExists) return new StorageRouteSnapshotResolution.ProfileMissing();
        if (!row.ProfileStateIsKnown) return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileState);
        if (!row.ProfileIsActive) return new StorageRouteSnapshotResolution.ProfileNotActive();
        if (row.StorageProfileRevision <= 0 || row.ModeIsPinned && row.StorageProfileRevision != row.PinnedProfileRevision)
            return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileRevision);
        if (!row.ProfileRevisionExists) return new StorageRouteSnapshotResolution.ProfileRevisionMissing();
        if (!IsValidTypeKey(row.ProviderTypeKey)) return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProviderTypeKey);
        if (!IsValidFingerprint(row.NamespaceFingerprint)) return new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.NamespaceFingerprint);

        return new StorageRouteSnapshotResolution.Ready(new StorageRouteSnapshot
        {
            RouteId = row.RouteId,
            RouteRevision = row.RouteRevision,
            DataClassTypeKey = row.DataClassTypeKey,
            StorageProfileId = row.StorageProfileId,
            StorageProfileRevision = row.StorageProfileRevision,
            ProviderTypeKey = row.ProviderTypeKey!,
            NamespaceFingerprint = row.NamespaceFingerprint!,
        });
    }

    public static bool IsValidTypeKey(string? value) => value is { Length: > 0 and <= MaxTypeKeyLength } && TypeKeyPattern().IsMatch(value);

    private static bool IsValidMode(StorageRouteSnapshotRow row) =>
        row.ModeIsCurrentAtWrite && !row.ModeIsPinned && row.PinnedProfileRevision == null
        || !row.ModeIsCurrentAtWrite && row.ModeIsPinned && row.PinnedProfileRevision is > 0;

    private static bool IsValidFingerprint(string? value) => value is { Length: 71 } && NamespaceFingerprintPattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*/v[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeKeyPattern();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceFingerprintPattern();
}

internal sealed record StorageRouteSnapshotRow
{
    public required Guid RouteId { get; init; }
    public required int RouteRevision { get; init; }
    public required string DataClassTypeKey { get; init; }
    public required bool RouteStateIsKnown { get; init; }
    public required bool RouteIsActive { get; init; }
    public required bool RouteRevisionExists { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required bool ModeIsCurrentAtWrite { get; init; }
    public required bool ModeIsPinned { get; init; }
    public required int? PinnedProfileRevision { get; init; }
    public required bool ProfileExists { get; init; }
    public required bool ProfileStateIsKnown { get; init; }
    public required bool ProfileIsActive { get; init; }
    public required int StorageProfileRevision { get; init; }
    public required bool ProfileRevisionExists { get; init; }
    public required string? ProviderTypeKey { get; init; }
    public required string? NamespaceFingerprint { get; init; }
}
