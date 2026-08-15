using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

public sealed record ListStorageRoutePageQuery : IQuery<StoragePage<StorageRouteSummary>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = StoragePageLimits.DefaultPageSize;
}

public sealed record GetStorageRouteQuery : IQuery<StorageRouteDetail?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid RouteId { get; init; }
    public string? RevisionCursor { get; init; }
    public int RevisionLimit { get; init; } = StorageRouteRevisionPageLimits.DefaultPageSize;
}
