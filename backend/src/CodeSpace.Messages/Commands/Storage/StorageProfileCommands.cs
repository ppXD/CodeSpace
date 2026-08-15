using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

public sealed record CreateStorageProfileCommand : ICommand<StorageProfileDetail>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public required string StableName { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }
    public string? CredentialRef { get; init; }
}

public sealed record AppendStorageProfileRevisionCommand : ICommand<StorageProfileDetail?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedCurrentRevision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }
    public string? CredentialRef { get; init; }
}

public sealed record SetStorageProfileStateCommand : ICommand<StorageProfileDetail?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedCurrentRevision { get; init; }
    public required StorageProfileStateValue State { get; init; }
}

public sealed record ProbeStorageProfileCommand : ICommand<StorageProfileProbeResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
    public int? ProfileRevision { get; init; }
    public bool VerifyWriteAccess { get; init; } = true;
}
