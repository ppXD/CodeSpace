using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

public sealed record ListStorageProfilesQuery : IQuery<IReadOnlyList<StorageProfileSummary>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
}

public sealed record GetStorageProfileQuery : IQuery<StorageProfileDetail?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
}
