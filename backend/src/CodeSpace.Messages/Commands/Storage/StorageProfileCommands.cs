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

/// <summary>
/// Closes one bounded batch of the records a profile still holds, for a destination that can no longer serve them.
///
/// <para>Repeatable by design: <c>Remaining</c> says whether to call again. Nothing is taken on the caller's word —
/// each placement is settled only if the destination itself proves it cannot serve it.</para>
/// </summary>
public sealed record AbandonProfilePlacementsCommand : ICommand<ProfileAbandonmentSummary>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
    public int BatchSize { get; init; } = 50;
}

/// <summary>
/// Validates one bounded page of a sealed legacy manifest. Evidence must cover the whole manifest and retain a
/// confirmed destination witness before Minting can add a bounded page of sidecar CAS observations. The cursor is
/// bound to the profile's exact current revision; a changed revision is refused rather than silently adopted.
/// </summary>
public sealed record AdoptLegacyPlacementsCommand : ICommand<LegacyPlacementAdoptionSummary>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;
    public Guid ProfileId { get; init; }
    public int BatchSize { get; init; } = LegacyPlacementAdoptionLimits.DefaultRowsPerPass;
    public string? Cursor { get; init; }
}
