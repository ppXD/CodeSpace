using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Credentials;

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
/// Double-check after both locks handle the case where another caller (in-process or
/// cross-process) just refreshed while we were waiting.
/// </summary>
public sealed class OAuthTokenRefresher : IOAuthTokenRefresher, IScopedDependency
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    private readonly IOAuthClientRegistry _oauthClients;
    private readonly ICredentialPayloadWriter _payloadWriter;
    private readonly ICredentialResolver _credentialResolver;
    private readonly IPayloadEncryptor _encryptor;
    private readonly TimeProvider _clock;
    private readonly ICrossProcessLock _crossProcessLock;

    public OAuthTokenRefresher(IOAuthClientRegistry oauthClients, ICredentialPayloadWriter payloadWriter, ICredentialResolver credentialResolver, IPayloadEncryptor encryptor, TimeProvider clock, ICrossProcessLock crossProcessLock)
    {
        _oauthClients = oauthClients;
        _payloadWriter = payloadWriter;
        _credentialResolver = credentialResolver;
        _encryptor = encryptor;
        _clock = clock;
        _crossProcessLock = crossProcessLock;
    }

    public async Task<OAuthPayload> RefreshIfNeededAsync(ProviderContext context, OAuthPayload current, CancellationToken cancellationToken)
    {
        if (!ShouldRefresh(current)) return current;

        var sem = RefreshLocks.GetOrAdd(context.Credential.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var crossLock = await _crossProcessLock.AcquireAsync(CredentialIdToLockKey(context.Credential.Id), cancellationToken).ConfigureAwait(false);

            // Double-check: a sibling request may have refreshed while we waited. Re-decrypt
            // the credential's current on-disk payload rather than trust the stale `current`.
            var latest = _credentialResolver.Resolve(context.Credential) as OAuthPayload
                ?? throw new InvalidOperationException($"Credential {context.Credential.Id} is no longer an OAuth payload after lock acquisition");

            if (!ShouldRefresh(latest)) return latest;

            var refreshed = await ExchangeRefreshAsync(context, latest, cancellationToken).ConfigureAwait(false);

            await _payloadWriter.UpdatePayloadAsync(context.Credential, refreshed, cancellationToken).ConfigureAwait(false);

            return refreshed;
        }
        finally
        {
            sem.Release();
        }
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
    private static long CredentialIdToLockKey(Guid credentialId) => BitConverter.ToInt64(credentialId.ToByteArray(), 0);
}
