using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

/// <summary>Every routed data class and where this team stands on the deployment default for it.</summary>
public sealed record ListStorageAdoptionsQuery : IQuery<IReadOnlyList<StorageAdoptionStatus>>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
}
