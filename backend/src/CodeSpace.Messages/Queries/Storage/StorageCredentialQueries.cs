using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

public sealed record ListStorageCredentialsQuery : IQuery<IReadOnlyList<StorageCredentialMetadata>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
}

public sealed record ListStorageCredentialPageQuery : IQuery<StoragePage<StorageCredentialMetadata>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = StoragePageLimits.DefaultPageSize;
}

public sealed record GetStorageCredentialQuery : IQuery<StorageCredentialMetadata?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid CredentialId { get; init; }
}
