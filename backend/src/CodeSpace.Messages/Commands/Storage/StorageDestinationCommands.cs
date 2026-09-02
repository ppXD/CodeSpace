using System.Text.Json;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// Creates a whole destination - its key, its address, and what lands in it - as ONE step.
///
/// <para>Composed rather than left to the caller because the pieces are individually irreversible and collectively
/// pointless: a credential cannot be deleted, a profile cannot be deleted, and a profile with no route routes
/// nothing. A caller that issued the five underlying requests in sequence and lost the third would leave exactly the
/// half-built, un-removable state this command exists to make impossible - every <c>ICommand</c> runs inside one
/// explicit transaction, so this either produces a destination or produces nothing.</para>
///
/// <para>The secret is write-only request material and is never part of a response DTO. Qualify it first with
/// <see cref="ProbeStorageConfigurationCommand"/>: this command's job is to record a destination, not to discover
/// whether it works.</para>
/// </summary>
public sealed record CreateStorageDestinationCommand : ICommand<StorageDestinationDetail>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.StorageManage;

    /// <summary>Names the destination, and names the credential and profile underneath it, so all three read as one thing.</summary>
    public required string Name { get; init; }

    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }

    /// <summary>Omitted only for a provider whose secret schema requires nothing.</summary>
    public JsonElement? Secret { get; init; }

    /// <summary>A non-secret reminder of WHICH key this is, for a screen to show later. Never the secret itself.</summary>
    public string? SafeHint { get; init; }

    /// <summary>
    /// The data classes whose next write should land here. A class nobody has routed yet is routed and activated; a
    /// class already routed is repointed, which moves where its NEXT write lands and never moves stored bytes. Empty
    /// records the destination without sending anything to it.
    /// </summary>
    public IReadOnlyList<string> DataClassTypeKeys { get; init; } = [];
}
