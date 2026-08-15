using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

public sealed record CreateStorageRouteCommand : ICommand<StorageRouteDetail>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public required string DataClassTypeKey { get; init; }
    public required Guid StorageProfileId { get; init; }
    public StorageProfileRevisionModeValue ProfileRevisionMode { get; init; } = StorageProfileRevisionModeValue.CurrentAtWrite;
    public int? PinnedProfileRevision { get; init; }
}

public sealed record AppendStorageRouteRevisionCommand : ICommand<StorageRouteDetail?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid RouteId { get; init; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedCurrentRevision { get; init; }
    public required Guid StorageProfileId { get; init; }
    public StorageProfileRevisionModeValue ProfileRevisionMode { get; init; } = StorageProfileRevisionModeValue.CurrentAtWrite;
    public int? PinnedProfileRevision { get; init; }
}

public sealed record SetStorageRouteStateCommand : ICommand<StorageRouteDetail?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid RouteId { get; init; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedCurrentRevision { get; init; }
    public required StorageRouteStateValue State { get; init; }
}
