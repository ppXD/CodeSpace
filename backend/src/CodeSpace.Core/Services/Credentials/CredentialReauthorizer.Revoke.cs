using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialReauthorizer
{
    /// <summary>
    /// Once the unit of work commits, the tokens this re-authorization replaced are ended at the provider: leaving a live
    /// access token and a refresh token behind that nothing in CodeSpace holds any longer is a credential nobody can revoke.
    /// Only those tokens — never the user's whole grant, which on GitHub also carries their tokens in every other team
    /// connected through the same OAuth App and would end the new token as well — and never a token the new authorization
    /// shares. Best effort: the new token is stored and the old one is gone from the row whatever the provider answers.
    ///
    /// <para>After the commit, not at the write: a provider call inside the unit of work would hold what it holds — the
    /// owner's membership, for one — for as long as the provider takes to answer. The cost is the rare unit of work that fails
    /// after the write landed: the person repeats the re-authorization, which ends the pair that one wrote, and the pair
    /// the failed one replaced is left to expire at the provider.</para>
    /// </summary>
    private async Task RevokeReplacedTokensAfterCommitAsync(CancellationToken cancellationToken)
    {
        var replaced = ReplacedPairToRevoke(ReadReplacedPayload(), _ctx.Request.Payload);

        if (replaced == null || string.IsNullOrWhiteSpace(_ctx.Instance.OauthClientSecretEnc)) return;

        var instance = _ctx.Instance;
        var clientSecret = _encryptor.Decrypt(instance.OauthClientSecretEnc);
        var credentialId = _ctx.Credential.Id;

        await _postCommit.RunAfterCommitAsync(ct => RevokeAtProviderBestEffortAsync(instance, clientSecret, replaced, credentialId, ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task RevokeAtProviderBestEffortAsync(ProviderInstance instance, string clientSecret, OAuthPayload replaced, Guid credentialId, CancellationToken cancellationToken)
    {
        try
        {
            await _grants.RevokeAsync(instance, clientSecret, replaced, wholeGrant: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not revoke at the provider the tokens credential {CredentialId} held before it was re-authorized; the credential holds the new ones", credentialId);
        }
    }

    /// <summary>What the row held when it was replaced, read as the type it was then. A blank payload is a Disconnect that already ended its tokens; one that does not open leaves nothing to revoke with.</summary>
    private CredentialPayload? ReadReplacedPayload()
    {
        if (string.IsNullOrEmpty(_ctx.ReplacedEncryptedPayload)) return null;

        try
        {
            return _serializer.Deserialize(_ctx.PreviousAuthType, _encryptor.Decrypt(_ctx.ReplacedEncryptedPayload));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the payload credential {CredentialId} held before it was re-authorized, so its tokens are not revoked at the provider", _ctx.Credential.Id);

            return null;
        }
    }

    private CredentialReauthorizationResult BuildResult() => new() { CredentialId = _ctx.Credential.Id, ProviderUsername = _ctx.Profile?.AuthenticatedUserName ?? _ctx.Known?.ProviderUsername };
}
