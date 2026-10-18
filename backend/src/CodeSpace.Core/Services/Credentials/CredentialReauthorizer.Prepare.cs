using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialReauthorizer
{
    /// <summary>
    /// The credential by (id, the request's team), retired or not: a row of another team reads as not found, exactly as an
    /// id that names nothing, and a retired one is told apart below because it deserves its own answer.
    /// </summary>
    private async Task LoadCredentialAsync(CancellationToken cancellationToken)
    {
        var request = _ctx.Request;

        _ctx.Credential = await _db.Credential.AsNoTracking().Include(c => c.ProviderInstance)
            .FirstOrDefaultAsync(c => c.Id == request.CredentialId && c.TeamId == request.TeamId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"credential {request.CredentialId} not found");

        _ctx.PreviousAuthType = _ctx.Credential.AuthType;

        if (_ctx.Credential.ProviderInstanceId != request.ProviderInstanceId)
            throw new ArgumentException($"credential {request.CredentialId} belongs to another provider instance than the authorization was obtained from", nameof(request));
    }

    /// <summary>
    /// A soft-deleted row was retired — its connection removed, its person gone, its token shredded — and so is one whose
    /// person has no standing in the team (<see cref="TeamStanding"/>) and which the departed-token sweep retires next.
    /// Writing a live token into either would hand the team a token of someone without standing, and strand whatever the
    /// revival just brought back when the sweep takes it again.
    /// </summary>
    private async Task EnsureNotRetiredAsync(CancellationToken cancellationToken)
    {
        var credential = _ctx.Credential;

        if (credential.DeletedDate != null)
            throw CredentialRetiredException.SoftDeleted(credential.Id, credential.DisplayName);

        if (credential.Ownership != CredentialOwnership.Personal || credential.OwnerUserId is not Guid ownerId) return;

        if (await TeamStanding.HeldAsync(_db, credential.TeamId, ownerId, cancellationToken).ConfigureAwait(false)) return;

        throw Retired("the person it belongs to no longer has standing in the team, so the departed-token sweep would retire it again.");
    }

    private CredentialRetiredException Retired(string reason) => new(_ctx.Credential.Id, _ctx.Credential.DisplayName, reason);

    /// <summary>Sealed before anything is asked of the provider or any lock is held, so a key ring that cannot seal fails here and not with the lock held.</summary>
    private void SealNewPayload() => _ctx.NewEncryptedPayload = _encryptor.Encrypt(_serializer.Serialize(_ctx.Request.Payload));
}
