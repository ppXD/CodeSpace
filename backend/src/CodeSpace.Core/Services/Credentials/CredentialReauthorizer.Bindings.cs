using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialReauthorizer
{
    /// <summary>What the credential's dead token had broken comes back — the repositories it turned Error, the hooks it buried — with nothing re-pointed, because nothing moved.</summary>
    private async Task ReviveBindingsAsync(CancellationToken cancellationToken)
    {
        var revived = await _bindings.ReviveBindingsAsync(_ctx.Credential, cancellationToken).ConfigureAwait(false);

        if (revived.Any)
            _logger.LogInformation("Re-authorized credential {CredentialId} in place; {RepositoryCount} repositories and {ConnectionWebhookCount} group hooks bound through it came back", _ctx.Credential.Id, revived.RepositoryCount, revived.ConnectionWebhookCount);
    }

    /// <summary>
    /// The owner's act-as identity is pointed at this credential again — created if their Disconnect had ended it — as a
    /// connect does. Only when the owner is the one who re-authorized: an identity says "this account speaks for this
    /// person", and only the person can say that. Best-effort, as at connect: the credential is already valid for repository
    /// operations, and a probe hiccup right after a valid token must not undo a re-authorization that has been written.
    /// </summary>
    private async Task RelinkActorIdentityBestEffortAsync(CancellationToken cancellationToken)
    {
        if (!ActorIsTheOwner(out var ownerId)) return;

        try
        {
            await _identities.EnsureIdentityForCredentialAsync(_ctx.Instance, _ctx.Credential, ownerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Re-authorized credential {CredentialId} but could not relink the act-as identity of user {UserId}; act-as stays unavailable until they re-authorize again", _ctx.Credential.Id, ownerId);
        }
    }

    private bool ActorIsTheOwner(out Guid ownerId)
    {
        ownerId = _ctx.Credential.OwnerUserId ?? Guid.Empty;

        return _ctx.Credential.Ownership == CredentialOwnership.Personal && ownerId != Guid.Empty && ownerId == _ctx.Request.ActorUserId;
    }
}
