using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Destinations;

public sealed class StorageDestinationService : IStorageDestinationService
{
    private readonly IStorageCredentialService _credentials;
    private readonly IStorageProfileService _profiles;
    private readonly IStorageRouteService _routes;

    public StorageDestinationService(IStorageCredentialService credentials, IStorageProfileService profiles, IStorageRouteService routes)
    {
        _credentials = credentials;
        _profiles = profiles;
        _routes = routes;
    }

    public async Task<StorageDestinationDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageDestinationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var credential = await MintCredentialAsync(teamId, actorId, command, cancellationToken).ConfigureAwait(false);
        var profile = await CreateProfileAsync(teamId, actorId, command, credential, cancellationToken).ConfigureAwait(false);
        var active = await ActivateProfileAsync(teamId, actorId, profile, cancellationToken).ConfigureAwait(false);

        await ClaimDataClassesAsync(teamId, actorId, command.DataClassTypeKeys, active.Id, cancellationToken).ConfigureAwait(false);

        return Detail(command, credential, active);
    }

    /// <summary>
    /// Mints the key, unless this provider has no secret inputs at all - the one case with no credential, read off
    /// the provider's own schema by the credential service rather than decided here.
    /// </summary>
    private async Task<StorageCredentialMetadata?> MintCredentialAsync(Guid teamId, Guid actorId, CreateStorageDestinationCommand command, CancellationToken cancellationToken)
    {
        if (command.Secret == null || command.Secret.Value.ValueKind == JsonValueKind.Null) return null;

        return await _credentials.CreateAsync(teamId, actorId, new CreateStorageCredentialCommand
        {
            StableName = command.Name,
            ProviderTypeKey = command.ProviderTypeKey,
            Secret = command.Secret.Value,
            SafeHint = command.SafeHint,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The profile pins the credential by EXACT version. Nothing falls forward: rotating the key later changes
    /// nothing at runtime until a profile revision names the new version, which is why repair has to append to both.
    /// </summary>
    private async Task<StorageProfileDetail> CreateProfileAsync(Guid teamId, Guid actorId, CreateStorageDestinationCommand command, StorageCredentialMetadata? credential, CancellationToken cancellationToken) =>
        await _profiles.CreateAsync(teamId, actorId, new CreateStorageProfileCommand
        {
            StableName = command.Name,
            ProviderTypeKey = command.ProviderTypeKey,
            NonSecretConfig = command.NonSecretConfig,
            CredentialRef = credential?.CredentialRef,
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A profile is created Draft, and only an Active profile is admitted for a WRITE. Leaving it Draft would record
    /// a destination that silently refuses every artifact.
    /// </summary>
    private async Task<StorageProfileDetail> ActivateProfileAsync(Guid teamId, Guid actorId, StorageProfileDetail profile, CancellationToken cancellationToken)
    {
        var active = await _profiles.SetStateAsync(teamId, actorId, new SetStorageProfileStateCommand
        {
            ProfileId = profile.Id,
            ExpectedXmin = profile.Xmin,
            ExpectedCurrentRevision = profile.CurrentRevision,
            State = StorageProfileStateValue.Active,
        }, cancellationToken).ConfigureAwait(false);

        return active ?? throw new InvalidOperationException($"Storage profile {profile.Id:D} vanished between its own creation and activation inside one transaction.");
    }

    private async Task ClaimDataClassesAsync(Guid teamId, Guid actorId, IReadOnlyList<string> dataClassTypeKeys, Guid profileId, CancellationToken cancellationToken)
    {
        foreach (var dataClassTypeKey in dataClassTypeKeys.Distinct(StringComparer.Ordinal))
            await ClaimDataClassAsync(teamId, actorId, dataClassTypeKey, profileId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A data class carries exactly one route row for the life of the team - the route service refuses a second one -
    /// so claiming a class is "create it" only the first time and "repoint it" every time after. Both move where the
    /// NEXT write lands and neither moves a stored byte.
    /// </summary>
    private async Task ClaimDataClassAsync(Guid teamId, Guid actorId, string dataClassTypeKey, Guid profileId, CancellationToken cancellationToken)
    {
        var existing = await _routes.GetByDataClassAsync(teamId, dataClassTypeKey, cancellationToken).ConfigureAwait(false);

        var route = existing == null
            ? await CreateRouteAsync(teamId, actorId, dataClassTypeKey, profileId, cancellationToken).ConfigureAwait(false)
            : await RepointRouteAsync(teamId, actorId, existing, profileId, cancellationToken).ConfigureAwait(false);

        if (route.State == StorageRouteStateValue.Active) return;

        await ActivateRouteAsync(teamId, actorId, route, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RouteHead> CreateRouteAsync(Guid teamId, Guid actorId, string dataClassTypeKey, Guid profileId, CancellationToken cancellationToken)
    {
        var created = await _routes.CreateAsync(teamId, actorId, new CreateStorageRouteCommand
        {
            DataClassTypeKey = dataClassTypeKey,
            StorageProfileId = profileId,
        }, cancellationToken).ConfigureAwait(false);

        return new RouteHead(created.Id, created.Xmin, created.CurrentRevision, created.State);
    }

    private async Task<RouteHead> RepointRouteAsync(Guid teamId, Guid actorId, StorageRouteSummary existing, Guid profileId, CancellationToken cancellationToken)
    {
        if (existing.StorageProfileId == profileId) return new RouteHead(existing.Id, existing.Xmin, existing.CurrentRevision, existing.State);

        var repointed = await _routes.AppendRevisionAsync(teamId, actorId, new AppendStorageRouteRevisionCommand
        {
            RouteId = existing.Id,
            ExpectedXmin = existing.Xmin,
            ExpectedCurrentRevision = existing.CurrentRevision,
            StorageProfileId = profileId,
        }, cancellationToken).ConfigureAwait(false);

        if (repointed == null) throw new InvalidOperationException($"Storage route {existing.Id:D} vanished while being repointed inside one transaction.");

        return new RouteHead(repointed.Id, repointed.Xmin, repointed.CurrentRevision, repointed.State);
    }

    /// <summary>
    /// Activation is where the route service writes and discards one real object at the destination. It is the last
    /// gate before this team's artifacts start going somewhere new, and it runs inside this transaction - so a
    /// destination that cannot take bytes leaves no destination behind at all.
    /// </summary>
    private async Task ActivateRouteAsync(Guid teamId, Guid actorId, RouteHead route, CancellationToken cancellationToken)
    {
        var activated = await _routes.SetStateAsync(teamId, actorId, new SetStorageRouteStateCommand
        {
            RouteId = route.Id,
            ExpectedXmin = route.Xmin,
            ExpectedCurrentRevision = route.CurrentRevision,
            State = StorageRouteStateValue.Active,
        }, cancellationToken).ConfigureAwait(false);

        if (activated == null) throw new InvalidOperationException($"Storage route {route.Id:D} vanished between its own creation and activation inside one transaction.");
    }

    private static StorageDestinationDetail Detail(CreateStorageDestinationCommand command, StorageCredentialMetadata? credential, StorageProfileDetail profile) => new()
    {
        ProfileId = profile.Id,
        Name = profile.StableName,
        ProviderTypeKey = command.ProviderTypeKey,
        ProfileRevision = profile.CurrentRevision,
        State = profile.State,
        CredentialId = credential?.Id,
        CredentialRevision = credential?.CurrentRevision,
        DataClassTypeKeys = command.DataClassTypeKeys.Distinct(StringComparer.Ordinal).ToList(),
    };

    private sealed record RouteHead(Guid Id, uint Xmin, int CurrentRevision, StorageRouteStateValue State);
}
