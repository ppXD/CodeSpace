using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>The secret is write-only request material and is never part of a response DTO.</summary>
public sealed class CreateStorageCredentialCommand : ICommand<StorageCredentialMetadata>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public required string StableName { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement Secret { get; init; }
    public string? SafeHint { get; init; }
}

/// <summary>The route-owned id selects a stable credential; rotation appends one immutable encrypted revision.</summary>
public sealed class AppendStorageCredentialRevisionCommand : ICommand<StorageCredentialMetadata?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid CredentialId { get; set; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedCurrentRevision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement Secret { get; init; }
    public string? SafeHint { get; init; }
}

public sealed class RevokeStorageCredentialCommand : ICommand<StorageCredentialMetadata?>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid CredentialId { get; set; }
    public required uint ExpectedXmin { get; init; }
    public required int ExpectedCurrentRevision { get; init; }
}
