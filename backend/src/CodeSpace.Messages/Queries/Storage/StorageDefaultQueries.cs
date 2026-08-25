using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Storage;

/// <summary>
/// Every deployment default in this instance. Carries no team — the answer is identical for every caller who holds
/// the instance capability. Nothing consumes these templates yet; the materializer lane is the intended reader.
/// </summary>
public sealed record ListStorageDefaultsQuery : IQuery<IReadOnlyList<StorageDefaultSummary>>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.StorageDefaultsManage;
}

public sealed record GetStorageDefaultQuery : IQuery<StorageDefaultDetail?>, IRequireGlobalPermission
{
    public string RequiredGlobalPermission => Permissions.StorageDefaultsManage;
    public Guid DefaultId { get; init; }
}
