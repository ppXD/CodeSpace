using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialReauthorizer
{
    /// <summary>
    /// Asks the provider whose account the new token is — and, for a pasted token, whether it works at all, and when it
    /// expires. Probed as a row of its own that exists nowhere: the credential being replaced still holds the old pair, and an
    /// auth strategy that finds the new token near its expiry would otherwise refresh THAT row's stored pair with the old
    /// refresh token. Asked within the probe deadline the sweep and the add keep (<see cref="IBoundedCredentialProbe"/>),
    /// so a provider that stops answering costs this request its deadline and not the thread it holds.
    /// </summary>
    private async Task ProbeNewAuthorizationAsync(CancellationToken cancellationToken)
    {
        if (!_registry.TryGet<ICredentialProbeCapability>(_ctx.Instance.Provider, out _)) return;

        try
        {
            _ctx.Profile = await _probe.ProbeAsync(_ctx.Instance, TransientCredentialHolding(_ctx.NewEncryptedPayload), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _ctx.Profile = new CredentialProbeResult { IsValid = false, Error = ex.Message };
        }
    }

    private Credential TransientCredentialHolding(string encryptedPayload) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = _ctx.Credential.TeamId,
        ProviderInstanceId = _ctx.Credential.ProviderInstanceId,
        OwnerUserId = _ctx.Credential.OwnerUserId,
        Ownership = _ctx.Credential.Ownership,
        AuthType = _ctx.Request.Payload.Type,
        DisplayName = _ctx.Credential.DisplayName,
        EncryptedPayload = encryptedPayload,
        Status = CredentialStatus.Active
    };

    /// <summary>
    /// A token the person pasted must work. One the provider just issued for the code the person approved is presumed to,
    /// so a probe that could not run does not refuse it — unless the account has to be compared (<see cref="EnsureSameProviderAccount"/>).
    /// </summary>
    private void EnsureNewAuthorizationIsUsable()
    {
        if (_ctx.Request.Payload is OAuthPayload || _ctx.Profile is { IsValid: true }) return;

        throw new InvalidOperationException($"Could not validate the token against {_ctx.Instance.Provider}: {_ctx.Profile?.Error ?? "this provider cannot check a token"}");
    }

    /// <summary>
    /// The account a credential was connected as is remembered by the act-as identity that names it — which a Disconnect
    /// soft-deletes but keeps. An identity is one per (person, provider instance), though, and every connect re-points it at
    /// the newest credential, so an older credential of the same person — a token linked first and OAuth connected after, the
    /// pair the Providers modal itself produces — is named by nothing. The product's own model is one account per person per
    /// instance, so the account on record for it is the one the person's identity speaks for there: the identity that names
    /// this credential if any does, else the live one, else the newest that ended. A team-service credential is no person's
    /// account, and a person who never linked an identity has none on record: nothing to compare, and the identity this
    /// re-authorization links is what records it from now on.
    /// </summary>
    private async Task LoadKnownAccountAsync(CancellationToken cancellationToken)
    {
        var credential = _ctx.Credential;

        if (credential.Ownership != CredentialOwnership.Personal || credential.OwnerUserId is not Guid ownerId) return;

        var identity = await _db.UserProviderIdentity.AsNoTracking()
            .Where(i => i.CredentialId == credential.Id || (i.UserId == ownerId && i.ProviderInstanceId == credential.ProviderInstanceId))
            .OrderByDescending(i => i.CredentialId == credential.Id)
            .ThenByDescending(i => i.DeletedDate == null)
            .ThenByDescending(i => i.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (identity != null) _ctx.Known = new KnownAccount(identity.ProviderUserId, identity.ProviderUsername);
    }

    /// <summary>
    /// Another account is a new connection, not a re-authorization: everything bound through this credential acts as the
    /// account it was connected as. A provider that cannot say which account signed in cannot be compared, so when an account
    /// is on record the re-authorization waits for an answer rather than guess.
    /// </summary>
    private void EnsureSameProviderAccount()
    {
        if (_ctx.Known is not { } known) return;

        if (_ctx.Profile is not { IsValid: true } profile)
            throw new InvalidOperationException($"Could not confirm which {_ctx.Instance.Provider} account this authorization belongs to ({_ctx.Profile?.Error ?? "the provider gave no answer"}). Nothing was changed; try again.");

        if (Compare(known.ProviderUserId, known.ProviderUsername, profile.AuthenticatedUserExternalId, profile.AuthenticatedUserName) != AccountMatch.Different) return;

        throw new CredentialAccountMismatchException(_ctx.Credential.Id, _ctx.Credential.DisplayName, Name(known.ProviderUsername, known.ProviderUserId), Name(profile.AuthenticatedUserName, profile.AuthenticatedUserExternalId));
    }

    private static string Name(string? username, string? externalId) => !string.IsNullOrWhiteSpace(username) ? username : externalId ?? "an unnamed account";
}
