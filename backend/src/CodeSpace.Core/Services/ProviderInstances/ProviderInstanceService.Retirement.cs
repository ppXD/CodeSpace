using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.ProviderInstances;

/// <summary>
/// What deleting a connection does to the hooks and tokens connected through it. Its group hooks come down first, while
/// the tokens that registered them still open (<see cref="RetireConnectionWebhooksAsync"/>). Then each token is retired
/// the way every other retirement retires one (<see cref="Credentials.ICredentialRevocationService.RetireAsync"/>): token
/// blanked, Revoked, soft-deleted, repositories bound through it told they need a new credential, act-as identities
/// linked to it gone. The connection's own OAuth client secret is blanked by the delete. Nothing it held stays
/// decryptable in the database.
///
/// <para>Each OAuth credential's tokens are then revoked at the provider, best effort, and only once the delete has
/// committed: a provider asked from inside the transaction would have ended them even when the delete rolled back, leaving
/// Active credentials on a token the provider no longer honours. The tokens and the client secret that revoke needs are
/// read before they are blanked and held in memory until then. Only those tokens, never the user's whole grant: the grant
/// is the user's, per OAuth app, and another team connected through the same app holds tokens under it that this delete
/// must leave working — on GitHub, the grant delete would end them all.</para>
/// </summary>
public sealed partial class ProviderInstanceService
{
    /// <summary>
    /// The longest a delete waits on the provider for all of its revokes together. The delete has committed by then and
    /// the caller is still waiting for its answer; a provider that never answers — often the very reason a connection is
    /// being removed — must not hold that answer for a socket timeout per token.
    /// </summary>
    internal static readonly TimeSpan ProviderRevocationBudget = TimeSpan.FromSeconds(30);

    private const string RetiredByDelete = "Provider instance deleted";

    /// <summary>
    /// Takes the connection's group hooks out of service the way a switch back to per-repository scope does: each is
    /// deleted at the provider best effort, the Registered rows go, the rest are cancelled. It has to happen here, before
    /// <see cref="RetireCredentialsAsync"/>: a hook is deleted with the token that registered it, and once that token is
    /// blanked nothing could ever remove a hook still delivering the group's events. Like a forced unbind's hook teardown,
    /// the provider is asked from inside the transaction; a delete that then rolled back would leave a hook row naming a
    /// hook that is gone, never a live hook with no way to remove it.
    ///
    /// <para>Then every hook row the connection keeps loses its secret. None is in service, so no delivery is verified
    /// against it again — ingestion refuses a retired hook before it reads the secret.</para>
    /// </summary>
    private async Task RetireConnectionWebhooksAsync(ProviderInstance instance, CancellationToken cancellationToken)
    {
        var retired = await _connectionHooks.RetireAllAsync(instance, cancellationToken).ConfigureAwait(false);

        await ShredConnectionWebhookSecretsAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        if (retired > 0) _logger.LogInformation("Retired {ConnectionWebhookCount} group hooks of provider instance {ProviderInstanceId} before deleting it", retired, instance.Id);
    }

    private async Task ShredConnectionWebhookSecretsAsync(Guid providerInstanceId, CancellationToken cancellationToken) =>
        await _db.ConnectionWebhook
            .Where(w => w.ProviderInstanceId == providerInstanceId && w.SecretEnc != string.Empty)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.SecretEnc, string.Empty), cancellationToken)
            .ConfigureAwait(false);

    private async Task<int> RetireCredentialsAsync(ProviderInstance instance, CancellationToken cancellationToken)
    {
        var credentials = await LoadUnretiredCredentialsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        var grants = ReadGrants(instance, credentials);

        foreach (var credential in credentials) await RetireAsync(credential, cancellationToken).ConfigureAwait(false);

        await RevokeGrantsAfterCommitAsync(instance, grants, cancellationToken).ConfigureAwait(false);

        return credentials.Count;
    }

    /// <summary>Every credential of the connection that still holds a token: not soft-deleted, and not already Revoked — a Disconnect blanked those.</summary>
    private async Task<List<Credential>> LoadUnretiredCredentialsAsync(Guid providerInstanceId, CancellationToken cancellationToken) =>
        await _db.Credential
            .Where(c => c.ProviderInstanceId == providerInstanceId && c.DeletedDate == null && c.Status != CredentialStatus.Revoked)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task RetireAsync(Credential credential, CancellationToken cancellationToken)
    {
        await _revocation.RetireAsync(credential, cancellationToken).ConfigureAwait(false);

        credential.LastError = RetiredByDelete;
    }

    /// <summary>
    /// What revoking each OAuth credential's tokens at the provider takes, read while the credentials and the connection still hold it.
    /// Nothing when the connection has no OAuth app (only personal and group tokens, which no provider endpoint revokes),
    /// or when its client secret no longer opens.
    /// </summary>
    private OAuthGrants ReadGrants(ProviderInstance instance, IReadOnlyList<Credential> credentials)
    {
        var clientSecret = ReadClientSecret(instance);

        if (clientSecret == null) return OAuthGrants.None;

        var tokens = credentials
            .Where(c => c.AuthType == AuthType.OAuth)
            .Select(c => (c.Id, Payload: _grants.ReadPayload(c)))
            .Where(grant => grant.Payload != null)
            .Select(grant => (grant.Id, grant.Payload!))
            .ToList();

        return new OAuthGrants(clientSecret, tokens);
    }

    private string? ReadClientSecret(ProviderInstance instance)
    {
        if (string.IsNullOrWhiteSpace(instance.OauthClientId) || string.IsNullOrWhiteSpace(instance.OauthClientSecretEnc)) return null;

        try
        {
            return _encryptor.Decrypt(instance.OauthClientSecretEnc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The OAuth client secret of provider instance {ProviderInstanceId} does not open; its credentials are retired here but cannot be revoked at the provider", instance.Id);
            return null;
        }
    }

    private async Task RevokeGrantsAfterCommitAsync(ProviderInstance instance, OAuthGrants grants, CancellationToken cancellationToken)
    {
        if (grants.Tokens.Count == 0) return;

        await _postCommit.RunAfterCommitAsync(ct => RevokeGrantsAsync(instance, grants, ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task RevokeGrantsAsync(ProviderInstance instance, OAuthGrants grants, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ProviderRevocationBudget);

        foreach (var (credentialId, payload) in grants.Tokens) await RevokeGrantAsync(instance, grants.ClientSecret, credentialId, payload, budget.Token).ConfigureAwait(false);
    }

    private async Task RevokeGrantAsync(ProviderInstance instance, string clientSecret, Guid credentialId, OAuthPayload payload, CancellationToken cancellationToken)
    {
        try
        {
            await _grants.RevokeAsync(instance, clientSecret, payload, wholeGrant: false, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Revoked the OAuth tokens of credential {CredentialId} at the provider after its provider instance {ProviderInstanceId} was deleted", credentialId, instance.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Revoking the OAuth tokens of credential {CredentialId} at the provider failed after its provider instance {ProviderInstanceId} was deleted; the token is already gone from CodeSpace and stays valid at the provider until it expires or is revoked there", credentialId, instance.Id);
        }
    }

    /// <summary>
    /// The connection's client secret and each OAuth credential's tokens, in memory only, between the delete's read of
    /// them and the provider's revoke. A class, not a record: a record's generated <c>ToString</c> would print the secret.
    /// </summary>
    private sealed class OAuthGrants
    {
        public static readonly OAuthGrants None = new(string.Empty, Array.Empty<(Guid, OAuthPayload)>());

        public OAuthGrants(string clientSecret, IReadOnlyList<(Guid CredentialId, OAuthPayload Payload)> tokens)
        {
            ClientSecret = clientSecret;
            Tokens = tokens;
        }

        public string ClientSecret { get; }

        public IReadOnlyList<(Guid CredentialId, OAuthPayload Payload)> Tokens { get; }
    }
}
