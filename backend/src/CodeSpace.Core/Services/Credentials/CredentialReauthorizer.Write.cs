using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialReauthorizer
{
    /// <summary>How many times the write may find the row changed — a Disconnect blanking it — and read it again, before the person is told to retry.</summary>
    private const int MaxReplaceRounds = 3;

    /// <summary>
    /// The one durable write, under the lock a refresh takes — the in-process semaphore, then the advisory lock. Holding it,
    /// no refresher is between asking the provider and storing its answer, so none can rotate the pair being replaced or put
    /// a rotated pair over the new one; and the row is read AFTER the lock, because what it held before is stale by
    /// definition. The write is a compare-and-swap on that payload: a Disconnect does not take the lock and may blank the
    /// row meanwhile, which is read again, and a retirement refuses. The caller's cancellation is honoured while waiting for
    /// the lock and no longer once it is held — the authorization code is spent, and the token is the only copy of it. A pair
    /// this process kept because it could not store a rotation of the pair just replaced is dropped with it, under the same
    /// lock (<see cref="IKeptOAuthPairs"/>): it belongs to the grant that no longer is the credential's.
    /// </summary>
    private async Task ReplaceUnderRefreshLockAsync(CancellationToken cancellationToken)
    {
        await using var held = await CredentialRefreshLock.AcquireAsync(_crossProcessLock, _ctx.Credential.Id, cancellationToken).ConfigureAwait(false);

        for (var round = 0; round < MaxReplaceRounds; round++)
        {
            _ctx.ReplacedEncryptedPayload = await _payloadReader.ReadEncryptedPayloadAsync(_ctx.Credential.Id, CancellationToken.None).ConfigureAwait(false);

            if (await _writer.TryReplaceAsync(BuildReplacement(), CancellationToken.None).ConfigureAwait(false))
            {
                MarkReauthorized();
                _keptPairs.Discard(_ctx.Credential.Id);

                return;
            }

            await EnsureNotRetiredNowAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException($"The credential '{_ctx.Credential.DisplayName}' kept changing while its new authorization was being stored, so nothing was changed. Try again.");
    }

    private CredentialReplacement BuildReplacement() => new()
    {
        CredentialId = _ctx.Credential.Id,
        ExpectedEncryptedPayload = _ctx.ReplacedEncryptedPayload,
        EncryptedPayload = _ctx.NewEncryptedPayload,
        AuthType = _ctx.Request.Payload.Type,
        Scopes = _ctx.Request.Scopes ?? _ctx.Profile?.GrantedScopes,
        ExpiresDate = ExpiryOfNewAuthorization(),
        DisplayName = string.IsNullOrWhiteSpace(_ctx.Request.DisplayName) ? null : _ctx.Request.DisplayName,
        ModifiedBy = _ctx.Request.ActorUserId
    };

    /// <summary>
    /// The expiry the row is written with: an OAuth pair's is its access token's; a static token's is the one its provider just
    /// reported for it (<see cref="CredentialHealthDerivation.RecordedExpiry"/>), so the health the list derives is of THIS
    /// token — an expired one replaced by a good one reads fine at once, and a new one that expires soon says so. When the
    /// provider did not describe the token the expiry is unknown, which is null, and never the replaced token's.
    /// </summary>
    private DateTimeOffset? ExpiryOfNewAuthorization() => _ctx.Request.Payload is OAuthPayload oauth
        ? oauth.ExpiresAt
        : CredentialHealthDerivation.RecordedExpiry(_ctx.Request.Payload.Type, _ctx.Profile)?.ExpiresAt;

    /// <summary>A write that found nothing to change either lost to a retirement, which is the answer, or to a row that is merely different now, which is read again.</summary>
    private async Task EnsureNotRetiredNowAsync()
    {
        if (await _payloadReader.IsRetiredAsync(_ctx.Credential.Id, CancellationToken.None).ConfigureAwait(false))
            throw Retired("it was retired while its new authorization was being stored.");
    }

    /// <summary>Brings the untracked copy up to what the row now holds, for the steps that hand it to the identity link.</summary>
    private void MarkReauthorized()
    {
        var replacement = BuildReplacement();
        var credential = _ctx.Credential;

        credential.EncryptedPayload = replacement.EncryptedPayload;
        credential.AuthType = replacement.AuthType;
        credential.Scopes = replacement.Scopes?.ToList();
        credential.ExpiresDate = replacement.ExpiresDate;
        credential.DisplayName = replacement.DisplayName ?? credential.DisplayName;
        credential.Status = CredentialStatus.Active;
        credential.LastError = null;
        credential.LastValidatedDate = DateTimeOffset.UtcNow;
    }
}
