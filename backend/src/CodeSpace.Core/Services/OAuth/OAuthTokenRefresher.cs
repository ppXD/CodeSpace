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
///
/// From then on the new pair is the credential, and until a write lands it exists only here. So the key ring is
/// proven able to seal it BEFORE the provider is asked; the store is retried on fresh connections; a pair that still
/// cannot be stored is kept in this process, logged, and stored by the credential's next use; and a compare-and-swap lost
/// to a row that is not empty never drops it — the row's pair wins only if it is at least as new.
/// </summary>
public sealed class OAuthTokenRefresher : IOAuthTokenRefresher, IScopedDependency
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    /// <summary>Waits between the attempts to store a rotated pair: one store and two retries, each on a connection of its own.</summary>
    private static readonly TimeSpan[] StoreRetryDelays = { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400) };

    /// <summary>How many times a store may find the row changed to an OLDER pair and take it over, before the pair is kept instead.</summary>
    private const int MaxStoreConflictRounds = 3;

    /// <summary>Plaintext for proving the key ring can seal before the provider is asked to rotate. Never stored, never logged.</summary>
    private const string SealProbe = "oauth-rotation-seal-probe";

    /// <summary>
    /// Pairs the provider issued that no attempt could store, by credential: what the row held when the refresh began
    /// (the compare-and-swap's expected value) and the pair. Only this process knows them; the next use of the credential in
    /// it stores them. Refreshes of one credential run one at a time per process, under <see cref="RefreshLocks"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, KeptRotation> KeptRotations = new();

    private readonly IOAuthClientRegistry _oauthClients;
    private readonly ICredentialPayloadWriter _payloadWriter;
    private readonly ICredentialPayloadReader _payloadReader;
    private readonly ICredentialResolver _credentialResolver;
    private readonly IPayloadEncryptor _encryptor;
    private readonly TimeProvider _clock;
    private readonly ICrossProcessLock _crossProcessLock;
    private readonly ITrackedCredentialEntry _trackedEntry;
    private readonly ILogger<OAuthTokenRefresher> _logger;

    public OAuthTokenRefresher(IOAuthClientRegistry oauthClients, ICredentialPayloadWriter payloadWriter, ICredentialPayloadReader payloadReader, ICredentialResolver credentialResolver, IPayloadEncryptor encryptor, TimeProvider clock, ICrossProcessLock crossProcessLock, ITrackedCredentialEntry trackedEntry, ILogger<OAuthTokenRefresher> logger)
    {
        _oauthClients = oauthClients;
        _payloadWriter = payloadWriter;
        _payloadReader = payloadReader;
        _credentialResolver = credentialResolver;
        _encryptor = encryptor;
        _clock = clock;
        _crossProcessLock = crossProcessLock;
        _trackedEntry = trackedEntry;
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
        var stored = await ReadStoredAfterKeptPairAsync(context, cancellationToken).ConfigureAwait(false);
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
    /// The row as stored now. A pair this process kept for the credential — the provider rotated it and every attempt to
    /// store it failed — is stored first, or found superseded by a newer one, so what comes back is a row that holds it
    /// and the refresh decides from that, rather than presenting the refresh token the provider has consumed.
    /// </summary>
    private async Task<string> ReadStoredAfterKeptPairAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        var stored = await _payloadReader.ReadEncryptedPayloadAsync(context.Credential.Id, cancellationToken).ConfigureAwait(false);

        if (!KeptRotations.TryGetValue(context.Credential.Id, out var kept)) return stored;

        await PersistOrAdoptAsync(context, kept.Expected, kept.Issued).ConfigureAwait(false);

        return await _payloadReader.ReadEncryptedPayloadAsync(context.Credential.Id, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes and stores the new pair only if the row still holds <paramref name="stored"/>. If it does not, the row
    /// changed while the provider answered (see <see cref="PersistOrAdoptAsync"/>). None of it honours the caller's
    /// cancellation (see the class summary).
    /// </summary>
    private async Task<OAuthPayload> RotateAndStoreAsync(ProviderContext context, string stored, OAuthPayload latest)
    {
        EnsureRotatedPairCanBeSealed();

        var refreshed = await ExchangeRefreshAsync(context, latest, CancellationToken.None).ConfigureAwait(false);

        return await PersistOrAdoptAsync(context, stored, refreshed).ConfigureAwait(false);
    }

    /// <summary>
    /// The writer seals the new pair only after the provider has issued it, so a Data Protection fault there — a key-ring
    /// store that is down, a key that cannot be created — would lose a pair already issued. Hitting the same fault here
    /// fails the refresh while the old refresh token is still good.
    /// </summary>
    private void EnsureRotatedPairCanBeSealed() => _encryptor.Encrypt(SealProbe);

    /// <summary>
    /// Stores <paramref name="issued"/> over <paramref name="expected"/>, the payload the refresh began from. When the row no
    /// longer holds that, nothing the provider issued is ever silently dropped:
    /// an emptied row is a revocation — the pair is revoked at the provider rather than written into the revoked row; a row
    /// that holds this very pair, or one at least as new (see <see cref="IsOlderThan"/>), is adopted; a row that holds an
    /// older one — a stale copy put back — is stored over, since the pair just issued is the one the provider honours.
    ///
    /// <para>The pair is kept (see <see cref="KeepForLater"/>) on every other way out: a store nobody answers, a row that
    /// cannot be read or resolved after the swap was lost, rows that keep changing to older pairs. Whatever fails in between,
    /// the pair is stored, superseded, revoked, or kept — never just gone.</para>
    /// </summary>
    private async Task<OAuthPayload> PersistOrAdoptAsync(ProviderContext context, string expected, OAuthPayload issued)
    {
        KeptRotations.TryRemove(context.Credential.Id, out _);

        try
        {
            for (var round = 0; round < MaxStoreConflictRounds; round++)
            {
                if (await TryStoreAsync(context, expected, issued).ConfigureAwait(false)) return issued;

                expected = await _payloadReader.ReadEncryptedPayloadAsync(context.Credential.Id, CancellationToken.None).ConfigureAwait(false);

                if (string.IsNullOrEmpty(expected)) await RevokeIssuedTokensAsync(context, issued).ConfigureAwait(false);

                var theirs = Adopt(context.Credential, expected);

                if (!IsOlderThan(theirs, issued)) return theirs;
            }
        }
        catch (Exception ex) when (!string.IsNullOrEmpty(expected))
        {
            // An emptied row is the revocation above — the pair already ended at the provider, and Adopt reports it. Anything
            // else leaves a pair that is stored nowhere.
            KeepForLater(context, expected, issued, ex);
            throw;
        }

        return KeepForLater(context, expected, issued);
    }

    /// <summary>
    /// One store and two retries, each on a connection of its own and none honouring the caller's cancellation. False when
    /// the compare-and-swap found the row changed. When no attempt gets an answer the failure surfaces, to
    /// <see cref="PersistOrAdoptAsync"/>, which keeps the pair for the credential's next use.
    /// </summary>
    private async Task<bool> TryStoreAsync(ProviderContext context, string expected, OAuthPayload issued)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _payloadWriter.UpdatePayloadAsync(context.Credential, expected, issued, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < StoreRetryDelays.Length)
            {
                _logger.LogWarning(ex, "Storing the rotated pair for credential {CredentialId} failed (attempt {Attempt} of {Attempts}); retrying", context.Credential.Id, attempt + 1, StoreRetryDelays.Length + 1);

                await Task.Delay(StoreRetryDelays[attempt]).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The pair is the credential now, held by nothing but this process: say so, and have the credential's next use store it.
    /// Returned so a caller that has no failure to surface can still hand the caller a live token.
    /// </summary>
    private OAuthPayload KeepForLater(ProviderContext context, string expected, OAuthPayload issued, Exception? failure = null)
    {
        KeptRotations[context.Credential.Id] = new KeptRotation(expected, issued);

        _logger.LogError(failure, "rotated pair NOT stored for credential {CredentialId}; this process keeps it and stores it on the credential's next use", context.Credential.Id);

        return issued;
    }

    /// <summary>
    /// Whether <paramref name="stored"/> is a pair the provider has superseded with <paramref name="issued"/>: a different
    /// pair that expires earlier. The same pair — a store whose acknowledgement was lost — is not, and nor is one that
    /// expires at the same moment or later, so a tie keeps what the row holds. Along one grant every refresh expires later
    /// than the one before; pairs of different grants (a reconnect) are ordered the same way by when they were issued.
    /// </summary>
    private static bool IsOlderThan(OAuthPayload stored, OAuthPayload issued) =>
        (stored.AccessToken != issued.AccessToken || stored.RefreshToken != issued.RefreshToken) && (stored.ExpiresAt ?? DateTimeOffset.MinValue) < (issued.ExpiresAt ?? DateTimeOffset.MinValue);

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
        _trackedEntry.MarkPayloadStored(credential);

        return _credentialResolver.Resolve(credential) as OAuthPayload
            ?? throw new InvalidOperationException($"Credential {credential.Id} is no longer an OAuth payload after lock acquisition");
    }

    /// <summary>
    /// The pair the provider issued for a credential revoked meanwhile is stored nowhere; revoke it there too, the way the
    /// write that emptied the row ends tokens. A Disconnect ends the user's grant, so the pair goes with it and the grant
    /// the user disconnected does not stay live. A retirement — a connection delete, a departure, a relink — soft-deletes
    /// the row and ends tokens, never the grant: on GitHub the grant also carries the user's tokens in every other team
    /// connected through the same OAuth App. Best effort, like the revocation itself.
    /// </summary>
    private async Task RevokeIssuedTokensAsync(ProviderContext context, OAuthPayload issued)
    {
        try
        {
            var wholeGrant = !await _payloadReader.IsRetiredAsync(context.Credential.Id, CancellationToken.None).ConfigureAwait(false);

            await RevokeAtProviderAsync(context.Instance, issued.RefreshToken!, "refresh_token", wholeGrant).ConfigureAwait(false);
            await RevokeAtProviderAsync(context.Instance, issued.AccessToken, "access_token", wholeGrant).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not revoke the tokens issued for credential {CredentialId}, which was revoked while it refreshed", context.Credential.Id);
        }
    }

    private async Task RevokeAtProviderAsync(ProviderInstance instance, string token, string tokenTypeHint, bool wholeGrant)
    {
        var input = new OAuthRevokeInput { Instance = instance, ClientId = instance.OauthClientId!, ClientSecret = _encryptor.Decrypt(instance.OauthClientSecretEnc!), Token = token, TokenTypeHint = tokenTypeHint, WholeGrant = wholeGrant };

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

    private sealed record KeptRotation(string Expected, OAuthPayload Issued);
}
