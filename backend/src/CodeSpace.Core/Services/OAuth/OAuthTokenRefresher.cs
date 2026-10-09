using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.OAuth;

public interface IOAuthTokenRefresher
{
    Task<OAuthPayload> RefreshIfNeededAsync(ProviderContext context, OAuthPayload current, CancellationToken cancellationToken);
}

/// <summary>
/// Shared refresh logic for any provider's OAuth strategy. Two-tier lock:
///   1. Per-credential in-process <see cref="SemaphoreSlim"/> — cheap, blocks sibling
///      requests in the same process.
///   2. <see cref="ICrossProcessLock"/> — coordinates across multiple API instances. Held
///      only for the refresh + write, then released by disposing the handle.
///
/// Under both locks it decides from the STORED row (<see cref="ICredentialPayloadReader"/>), never
/// the caller's entity: that was loaded before the lock, so when another caller (in-process or
/// cross-process) refreshed while we waited, it still carries the refresh token the provider
/// rotated away, and refreshing with it is refused with invalid_grant.
///
/// Once it asks the provider it no longer honours the caller's cancellation: the provider may
/// already have consumed the old refresh token, and a rotated pair that is never stored leaves
/// the row holding a token no later run can refresh with. The HTTP client's timeout bounds it.
/// </summary>
public sealed class OAuthTokenRefresher : IOAuthTokenRefresher, IScopedDependency
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly IOAuthClientRegistry _oauthClients;
    private readonly ICredentialPayloadWriter _payloadWriter;
    private readonly ICredentialPayloadReader _payloadReader;
    private readonly ICredentialResolver _credentialResolver;
    private readonly IPayloadEncryptor _encryptor;
    private readonly TimeProvider _clock;
    private readonly ICrossProcessLock _crossProcessLock;
    private readonly ILogger<OAuthTokenRefresher> _logger;

    public OAuthTokenRefresher(IOAuthClientRegistry oauthClients, ICredentialPayloadWriter payloadWriter, ICredentialPayloadReader payloadReader, ICredentialResolver credentialResolver, IPayloadEncryptor encryptor, TimeProvider clock, ICrossProcessLock crossProcessLock, ILogger<OAuthTokenRefresher> logger)
    {
        _oauthClients = oauthClients;
        _payloadWriter = payloadWriter;
        _payloadReader = payloadReader;
        _credentialResolver = credentialResolver;
        _encryptor = encryptor;
        _clock = clock;
        _crossProcessLock = crossProcessLock;
        _logger = logger;
    }

    public async Task<OAuthPayload> RefreshIfNeededAsync(ProviderContext context, OAuthPayload current, CancellationToken cancellationToken)
    {
        if (!ShouldRefresh(current)) return current;

        var sem = RefreshLocks.GetOrAdd(context.Credential.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var crossLock = await _crossProcessLock.AcquireAsync(CredentialIdToLockKey(context.Credential.Id), cancellationToken).ConfigureAwait(false);

            return await RefreshStoredPayloadAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Runs under the lock. Refreshes only if the stored token still needs it. A refusal is final unless the row changed
    /// meanwhile: emptied by a revocation, which does not take the lock (reported as revoked), or — only when this
    /// refresher's lock session was lost mid-refresh — refreshed by the pod that took the lock over, whose token we use.
    /// </summary>
    private async Task<OAuthPayload> RefreshStoredPayloadAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var stored = await _payloadReader.ReadEncryptedPayloadAsync(context.Credential.Id, cancellationToken).ConfigureAwait(false);
        var latest = Adopt(context.Credential, stored);

        if (!ShouldRefresh(latest)) return latest;

        try
        {
            return await RotateAndStoreAsync(context, stored, latest).ConfigureAwait(false);
        }
        catch (OAuthExchangeException)
        {
            var storedSinceRefusal = await _payloadReader.ReadEncryptedPayloadAsync(context.Credential.Id, CancellationToken.None).ConfigureAwait(false);

            if (storedSinceRefusal == stored) throw;

            return Adopt(context.Credential, storedSinceRefusal);
        }
    }

    /// <summary>
    /// Refreshes and stores the new pair only if the row still holds <paramref name="stored"/>. If it does not, the row
    /// changed while the provider answered: a revocation emptied it — the pair just issued is then revoked at the provider
    /// rather than written into the revoked row — or, after a lost lock session, the pod that took the lock over stored its
    /// own. None of it honours the caller's cancellation (see the class summary).
    /// </summary>
    private async Task<OAuthPayload> RotateAndStoreAsync(ProviderContext context, string stored, OAuthPayload latest)
    {
        var refreshed = await ExchangeRefreshAsync(context, latest, CancellationToken.None).ConfigureAwait(false);

        if (await _payloadWriter.UpdatePayloadAsync(context.Credential, stored, refreshed, CancellationToken.None).ConfigureAwait(false)) return refreshed;

        var storedMeanwhile = await _payloadReader.ReadEncryptedPayloadAsync(context.Credential.Id, CancellationToken.None).ConfigureAwait(false);

        if (string.IsNullOrEmpty(storedMeanwhile)) await RevokeIssuedTokensAsync(context, refreshed).ConfigureAwait(false);

        return Adopt(context.Credential, storedMeanwhile);
    }

    /// <summary>
    /// Points the caller's entity at the stored payload — as <see cref="ICredentialPayloadWriter"/> does after a refresh —
    /// so later reads in its scope see the current token instead of queueing for the lock again, and returns it decrypted.
    /// An empty payload is a credential revoked meanwhile: refreshing it would write a live token back into a revoked row.
    /// </summary>
    private OAuthPayload Adopt(Credential credential, string storedPayload)
    {
        if (string.IsNullOrEmpty(storedPayload))
            throw new InvalidOperationException($"Credential {credential.Id} was revoked; reconnect it to use it again");

        credential.EncryptedPayload = storedPayload;

        return _credentialResolver.Resolve(credential) as OAuthPayload
            ?? throw new InvalidOperationException($"Credential {credential.Id} is no longer an OAuth payload after lock acquisition");
    }

    /// <summary>
    /// The pair the provider issued for a credential revoked meanwhile is stored nowhere; revoke it there too, so the grant
    /// the user disconnected does not stay live. Best effort, like the revocation itself.
    /// </summary>
    private async Task RevokeIssuedTokensAsync(ProviderContext context, OAuthPayload issued)
    {
        try
        {
            await RevokeAtProviderAsync(context.Instance, issued.RefreshToken!, "refresh_token").ConfigureAwait(false);
            await RevokeAtProviderAsync(context.Instance, issued.AccessToken, "access_token").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not revoke the tokens issued for credential {CredentialId}, which was revoked while it refreshed", context.Credential.Id);
        }
    }

    private async Task RevokeAtProviderAsync(ProviderInstance instance, string token, string tokenTypeHint)
    {
        var input = new OAuthRevokeInput { Instance = instance, ClientId = instance.OauthClientId!, ClientSecret = _encryptor.Decrypt(instance.OauthClientSecretEnc!), Token = token, TokenTypeHint = tokenTypeHint };

        await _oauthClients.Get(instance.Provider).RevokeAsync(input, CancellationToken.None).ConfigureAwait(false);
    }

    private bool ShouldRefresh(OAuthPayload payload)
    {
        if (payload.ExpiresAt == null) return false;
        if (string.IsNullOrEmpty(payload.RefreshToken)) return false;
        return payload.ExpiresAt.Value - _clock.GetUtcNow() < RefreshBuffer;
    }

    private async Task<OAuthPayload> ExchangeRefreshAsync(ProviderContext context, OAuthPayload current, CancellationToken cancellationToken)
    {
        var instance = context.Instance;

        if (string.IsNullOrWhiteSpace(instance.OauthClientId) || string.IsNullOrWhiteSpace(instance.OauthClientSecretEnc))
            throw new InvalidOperationException($"Provider instance {instance.Id} is missing OAuth client credentials; cannot refresh");

        var clientSecret = _encryptor.Decrypt(instance.OauthClientSecretEnc);
        var client = _oauthClients.Get(instance.Provider);

        var response = await client.RefreshAsync(new OAuthRefreshInput
        {
            Instance = instance,
            ClientId = instance.OauthClientId,
            ClientSecret = clientSecret,
            RefreshToken = current.RefreshToken!
        }, cancellationToken).ConfigureAwait(false);

        return new OAuthPayload
        {
            AccessToken = response.AccessToken,
            // Provider may or may not rotate the refresh_token. Use the new one if present,
            // otherwise keep the old one (GitHub OAuth Apps frequently omit it on refresh).
            RefreshToken = response.RefreshToken ?? current.RefreshToken,
            ExpiresAt = response.ExpiresAt ?? current.ExpiresAt
        };
    }

    /// <summary>
    /// Stable int64 derived from the credential GUID. pg_advisory_lock takes int8 (bigint).
    /// We take the first 8 bytes of the GUID — uniform distribution, collision probability
    /// is the same as a 64-bit random key. Same credential id always maps to same key.
    /// </summary>
    internal static long CredentialIdToLockKey(Guid credentialId) => BitConverter.ToInt64(credentialId.ToByteArray(), 0);
}
