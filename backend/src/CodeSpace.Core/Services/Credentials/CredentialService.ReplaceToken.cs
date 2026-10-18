using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>Replacing a credential's token in place — the pasted-token door onto <see cref="ICredentialReauthorizer"/>.</summary>
public sealed partial class CredentialService
{
    public async Task<ReplaceCredentialTokenResult> ReplaceTokenAsync(Guid credentialId, string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token must not be empty", nameof(token));

        var credential = await LoadTeamCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);

        await HoldOwnerInTeamAsync(credential, cancellationToken).ConfigureAwait(false);

        var result = await _reauthorizer.ReauthorizeAsync(new CredentialReauthorization
        {
            CredentialId = credential.Id,
            TeamId = credential.TeamId,
            ProviderInstanceId = credential.ProviderInstanceId,
            Payload = StaticTokenPayload(credential, token.Trim()),
            ActorUserId = _currentUser.Id!.Value
        }, cancellationToken).ConfigureAwait(false);

        return new ReplaceCredentialTokenResult { CredentialId = result.CredentialId, ProviderUsername = result.ProviderUsername };
    }

    /// <summary>
    /// By (id, the header team), whatever its state: the authorization marker already refuses a credential of another team,
    /// but not for an instance Admin, who passes it by role — so the team the caller works in is checked here too. A retired
    /// credential is found, and refused by the re-authorization with the answer it deserves.
    /// </summary>
    private async Task<Credential> LoadTeamCredentialAsync(Guid credentialId, CancellationToken cancellationToken) =>
        await _db.Credential.AsNoTracking().FirstOrDefaultAsync(c => c.Id == credentialId && c.TeamId == _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"credential {credentialId} not found");

    /// <summary>
    /// A person's token is theirs while they stand in the team, and the standing is held until this unit of work commits: a
    /// departure that starts now waits and then retires the new token with the rest of theirs, as it does for a link or a
    /// connect. A team-service credential has no person to ask about.
    /// </summary>
    private async Task HoldOwnerInTeamAsync(Credential credential, CancellationToken cancellationToken)
    {
        if (credential.Ownership != CredentialOwnership.Personal || credential.OwnerUserId is not Guid ownerId) return;

        if (!await _members.HoldMembershipAsync(credential.TeamId, ownerId, cancellationToken).ConfigureAwait(false))
            throw new CredentialRetiredException(credential.Id, credential.DisplayName, "the person it belongs to no longer has standing in the team.");
    }

    /// <summary>
    /// The payload a pasted token becomes: another of the kind the row already is when it is a static token — a group token
    /// stays a group token — and a personal access token when it replaces an OAuth sign-in. A key or a login is not a token.
    /// </summary>
    private static CredentialPayload StaticTokenPayload(Credential credential, string token) => credential.AuthType switch
    {
        AuthType.OAuth or AuthType.Pat => new PatPayload { Token = token },
        AuthType.ProjectAccessToken => new ProjectAccessTokenPayload { Token = token },
        AuthType.GroupAccessToken => new GroupAccessTokenPayload { Token = token },
        _ => throw new ArgumentException($"credential {credential.Id} is a {credential.AuthType} credential, which a pasted token cannot replace", nameof(credential))
    };
}
