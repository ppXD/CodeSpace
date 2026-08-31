using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

public sealed record ListStorageProfilesQuery : IQuery<IReadOnlyList<StorageProfileSummary>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
}

public sealed record ListStorageProfilePageQuery : IQuery<StoragePage<StorageProfileSummary>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = StoragePageLimits.DefaultPageSize;
}

public sealed record GetStorageProfileQuery : IQuery<StorageProfileDetail?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
}

public sealed record GetPlacementIntegrityQuery : IQuery<PlacementIntegritySummary>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
}

public sealed record ListProfilePlacementsQuery : IQuery<ProfilePlacementPage>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = StoragePageLimits.DefaultPageSize;
}

public sealed record GetProfilePlacementTotalsQuery : IQuery<IReadOnlyList<ProfilePlacementTotal>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
}

/// <summary>
/// A report-only pass over the artifact rows this team wrote BEFORE the CAS plane, asking whether one storage
/// profile can still name them. It writes nothing — no placement is minted, no row relinked, no byte moved.
/// </summary>
public sealed record GetLegacyPlacementSurveyQuery : IQuery<LegacyPlacementSurvey>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
    public int Limit { get; init; } = LegacyPlacementSurveyLimits.MaxRowsPerPass;
}
