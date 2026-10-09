using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// Pins refresh-or-not decision boundaries. The refresher must never refresh when:
/// (1) the token has no expiry, (2) no refresh_token is available, or (3) the access token
/// is still safely within its validity window. It must refresh when the access token is
/// near expiry AND a refresh_token is available.
///
/// <para>Under the lock it decides from the STORED row, never the caller's entity: the entity was
/// loaded before the lock, so once another run or pod has rotated the token it still carries the
/// consumed refresh token, and refreshing with that is refused (invalid_grant).</para>
///
/// <para>Once it asks the provider it finishes without the caller's cancellation, and stores the new pair only over the
/// payload it read — never into a row a revocation emptied meanwhile.</para>
/// </summary>
[Trait("Category", "Unit")]
public class OAuthTokenRefresherTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Returns_current_when_ExpiresAt_is_null()
    {
        var ctx = BuildContext();
        var current = new OAuthPayload { AccessToken = "a", RefreshToken = "r", ExpiresAt = null };

        var (result, exchanges, persists) = await RunAsync(current, ctx);

        result.AccessToken.ShouldBe("a");
        exchanges.ShouldBe(0);
        persists.ShouldBe(0);
    }

    [Fact]
    public async Task Returns_current_when_no_refresh_token_available()
    {
        var ctx = BuildContext();
        var current = new OAuthPayload { AccessToken = "a", RefreshToken = null, ExpiresAt = Now.AddSeconds(-1) };

        var (result, exchanges, persists) = await RunAsync(current, ctx);

        result.AccessToken.ShouldBe("a");
        exchanges.ShouldBe(0);
        persists.ShouldBe(0);
    }

    [Fact]
    public async Task Returns_current_when_token_is_safely_within_validity()
    {
        var ctx = BuildContext();
        // ExpiresAt is 30 minutes out — well outside the 2-min refresh buffer.
        var current = new OAuthPayload { AccessToken = "a", RefreshToken = "r", ExpiresAt = Now.AddMinutes(30) };

        var (result, exchanges, persists) = await RunAsync(current, ctx);

        result.AccessToken.ShouldBe("a");
        exchanges.ShouldBe(0);
        persists.ShouldBe(0);
    }

    [Fact]
    public async Task Refreshes_when_token_is_near_expiry()
    {
        var ctx = BuildContext();
        // ExpiresAt is 30 seconds out — inside the 2-min buffer.
        var current = new OAuthPayload { AccessToken = "old", RefreshToken = "r-old", ExpiresAt = Now.AddSeconds(30) };

        var (result, exchanges, persists) = await RunAsync(current, ctx, newAccessToken: "new", newRefreshToken: "r-new", newExpiresAt: Now.AddHours(2));

        result.AccessToken.ShouldBe("new");
        result.RefreshToken.ShouldBe("r-new");
        result.ExpiresAt.ShouldBe(Now.AddHours(2));
        exchanges.ShouldBe(1);
        persists.ShouldBe(1);
    }

    [Fact]
    public async Task Refresh_keeps_old_refresh_token_when_provider_omits_it()
    {
        // GitHub OAuth Apps sometimes return a refresh without rotating refresh_token; the
        // refresher must preserve the prior one rather than wipe it.
        var ctx = BuildContext();
        var current = new OAuthPayload { AccessToken = "old", RefreshToken = "r-keep", ExpiresAt = Now.AddSeconds(5) };

        var (result, _, _) = await RunAsync(current, ctx, newAccessToken: "new", newRefreshToken: null, newExpiresAt: Now.AddHours(1));

        result.AccessToken.ShouldBe("new");
        result.RefreshToken.ShouldBe("r-keep");
    }

    [Fact]
    public async Task Uses_the_stored_token_when_another_caller_refreshed_it_meanwhile()
    {
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-consumed", ExpiresAt = Now.AddSeconds(-1) };
        var row = new StoredRow(new OAuthPayload { AccessToken = "rotated", RefreshToken = "r-rotated", ExpiresAt = Now.AddHours(2) });

        var (result, exchanges, persists) = await RunAsync(stale, ctx, row, new RecordingOAuthClient("fresh", null, null));

        result.AccessToken.ShouldBe("rotated");
        exchanges.ShouldBe(0);
        persists.ShouldBe(0);
        ResolveEntity(ctx).AccessToken.ShouldBe("rotated", customMessage: "the caller's entity should carry the stored token, so later reads in its scope do not queue for the lock again");
    }

    [Fact]
    public async Task A_refresh_refused_after_the_lock_was_lost_uses_the_token_the_new_holder_stored()
    {
        // Nothing stores a token without holding the refresh lock, so only a lost lock session (a killed connection,
        // idle_session_timeout, a failover) gets here: the pod that took the lock over refreshed first and stored the
        // rotated pair while this refresh was refused.
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-0", ExpiresAt = Now.AddSeconds(-1) };
        var row = new StoredRow(stale);
        var provider = RecordingOAuthClient.Refusing(onRefusing: () => row.Store(new OAuthPayload { AccessToken = "rotated", RefreshToken = "r-1", ExpiresAt = Now.AddHours(2) }));

        var (result, exchanges, persists) = await RunAsync(stale, ctx, row, provider);

        result.AccessToken.ShouldBe("rotated");
        exchanges.ShouldBe(1);
        persists.ShouldBe(0);
    }

    [Fact]
    public async Task A_refresh_refused_because_the_credential_was_revoked_says_it_was_revoked()
    {
        // Revocation empties the row without taking the refresh lock — here while the provider refuses the refresh.
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-0", ExpiresAt = Now.AddSeconds(-1) };
        var row = new StoredRow(stale);
        var provider = RecordingOAuthClient.Refusing(onRefusing: row.Revoke);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(stale, ctx, row, provider));

        failure.Message.ShouldContain("was revoked");
        provider.RefreshCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_caller_cancelled_after_the_provider_rotated_still_stores_the_rotated_pair()
    {
        // The provider has consumed r-0 once it answers; a caller cancelled before the pair is stored must not leave the
        // row holding r-0, which every later refresh would present and be refused for.
        using var cancelled = new CancellationTokenSource();
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-0", ExpiresAt = Now.AddSeconds(-1) };
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2), onRefreshing: cancelled.Cancel);

        await RunAsync(stale, ctx, row, provider, cancelled.Token);

        row.Read().RefreshToken.ShouldBe("r-1");
    }

    [Fact]
    public async Task A_refused_refresh_with_nothing_newer_stored_rethrows_the_refusal()
    {
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-0", ExpiresAt = Now.AddSeconds(-1) };

        var refusal = await Should.ThrowAsync<OAuthExchangeException>(() => RunAsync(stale, ctx, new StoredRow(stale), RecordingOAuthClient.Refusing()));

        refusal.Error.ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task A_credential_revoked_while_the_provider_rotated_is_not_written_back()
    {
        // Revocation empties the row without taking the refresh lock — here while the provider answers the refresh. The
        // pair it issued must not land in the revoked row, and since nothing stores it, it is revoked at the provider.
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-0", ExpiresAt = Now.AddSeconds(-1) };
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("new", "r-1", Now.AddHours(2), onRefreshing: row.Revoke);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(stale, ctx, row, provider));

        failure.Message.ShouldContain("was revoked");
        row.IsEmpty.ShouldBeTrue();
        provider.RevokedTokens.ShouldBe(new[] { "r-1", "new" });
    }

    [Fact]
    public async Task A_refresh_whose_row_changed_meanwhile_uses_what_the_row_holds()
    {
        // Reachable only when this refresher's lock session was lost and the provider does not rotate refresh tokens, so
        // the pod that took the lock over refreshed with the same one, succeeded too, and stored first.
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-0", ExpiresAt = Now.AddSeconds(-1) };
        var row = new StoredRow(stale);
        var provider = new RecordingOAuthClient("mine", "r-0", Now.AddHours(2), onRefreshing: () => row.Store(new OAuthPayload { AccessToken = "theirs", RefreshToken = "r-0", ExpiresAt = Now.AddHours(2) }));

        var (result, exchanges, persists) = await RunAsync(stale, ctx, row, provider);

        result.AccessToken.ShouldBe("theirs");
        exchanges.ShouldBe(1);
        persists.ShouldBe(0);
        provider.RevokedTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_credential_revoked_while_waiting_is_not_refreshed()
    {
        // Revocation empties the stored payload. Refreshing anyway would write a live token back into a revoked row.
        var ctx = BuildContext();
        var stale = new OAuthPayload { AccessToken = "old", RefreshToken = "r-0", ExpiresAt = Now.AddSeconds(-1) };
        var row = new StoredRow(stale);
        row.Revoke();
        var provider = new RecordingOAuthClient("fresh", "r-new", Now.AddHours(1));

        await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(stale, ctx, row, provider));

        provider.RefreshCalls.ShouldBe(0);
    }

    // ── Test plumbing ──────────────────────────────────────────────────────────────

    private static ProviderContext BuildContext()
    {
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = Guid.NewGuid(),
            Provider = ProviderKind.GitLab,
            DisplayName = "test",
            BaseUrl = "https://gitlab.example",
            OauthClientId = "client",
            // Stub encryptor returns whatever it receives; this value will be "decrypted" to itself.
            OauthClientSecretEnc = "secret-enc"
        };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = instance.TeamId,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.OAuth,
            DisplayName = "cred",
            EncryptedPayload = string.Empty
        };

        return new ProviderContext(instance, credential);
    }

    private static Task<(OAuthPayload Result, int Exchanges, int Persists)> RunAsync(OAuthPayload current, ProviderContext ctx, string? newAccessToken = null, string? newRefreshToken = null, DateTimeOffset? newExpiresAt = null) =>
        RunAsync(current, ctx, new StoredRow(current), new RecordingOAuthClient(newAccessToken ?? "fresh", newRefreshToken, newExpiresAt));

    /// <summary>The caller's entity holds <paramref name="current"/> — what it loaded; <paramref name="row"/> is what the database holds now.</summary>
    private static async Task<(OAuthPayload Result, int Exchanges, int Persists)> RunAsync(OAuthPayload current, ProviderContext ctx, StoredRow row, RecordingOAuthClient oauthClient, CancellationToken cancellationToken = default)
    {
        ctx.Credential.EncryptedPayload = Serializer.Serialize(current);

        var registry = new OAuthClientRegistry(new IOAuthClient[] { oauthClient });
        var writer = new RecordingPayloadWriter(row);
        var resolver = new CredentialResolver(new IdentityEncryptor(), Serializer);

        var refresher = new OAuthTokenRefresher(registry, writer, row, resolver, new IdentityEncryptor(), new FixedClock(Now), new NoopCrossProcessLock(), NullLogger<OAuthTokenRefresher>.Instance);

        var result = await refresher.RefreshIfNeededAsync(ctx, current, cancellationToken);

        return (result, oauthClient.RefreshCalls, writer.PersistCalls);
    }

    private static readonly CredentialPayloadSerializer Serializer = new();

    private static OAuthPayload ResolveEntity(ProviderContext ctx) => (OAuthPayload)new CredentialResolver(new IdentityEncryptor(), Serializer).Resolve(ctx.Credential);

    /// <summary>The credential row as the database holds it; the identity encryptor makes its ciphertext the payload JSON.</summary>
    private sealed class StoredRow : ICredentialPayloadReader
    {
        private string _ciphertext;

        public StoredRow(OAuthPayload payload) { _ciphertext = Serializer.Serialize(payload); }

        public void Store(CredentialPayload payload) => _ciphertext = Serializer.Serialize(payload);

        public void Revoke() => _ciphertext = string.Empty;

        public bool IsEmpty => _ciphertext.Length == 0;

        public OAuthPayload Read() => (OAuthPayload)Serializer.Deserialize(AuthType.OAuth, _ciphertext);

        public Task<string> ReadEncryptedPayloadAsync(Guid credentialId, CancellationToken cancellationToken) => Task.FromResult(_ciphertext);

        /// <summary>The writer's compare-and-swap: stores only if the row still holds <paramref name="expected"/>.</summary>
        public bool StoreIfUnchanged(string expected, CredentialPayload payload)
        {
            if (_ciphertext != expected) return false;

            Store(payload);
            return true;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// The provider's token endpoint. Like an HTTP client, it honours the caller's cancellation once the provider has
    /// acted — the request went out, the answer is lost.
    /// </summary>
    private sealed class RecordingOAuthClient : IOAuthClient
    {
        private readonly string _accessToken;
        private readonly string? _refreshToken;
        private readonly DateTimeOffset? _expiresAt;
        private readonly Action? _refuse;
        private readonly Action? _onRefreshing;

        public int RefreshCalls;

        public RecordingOAuthClient(string accessToken, string? refreshToken, DateTimeOffset? expiresAt, Action? refuse = null, Action? onRefreshing = null)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _expiresAt = expiresAt;
            _refuse = refuse;
            _onRefreshing = onRefreshing;
        }

        /// <summary>A provider that refuses every refresh with invalid_grant, after running <paramref name="onRefusing"/>.</summary>
        public static RecordingOAuthClient Refusing(Action? onRefusing = null) => new("unused", null, null, onRefusing ?? (() => { }));

        public List<string> RevokedTokens { get; } = new();

        public ProviderKind Kind => ProviderKind.GitLab;

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken)
        {
            RefreshCalls++;

            if (_refuse != null)
            {
                _refuse();
                throw new OAuthExchangeException("invalid_grant", "The provided authorization grant is invalid", null);
            }

            _onRefreshing?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new OAuthTokenResponse
            {
                AccessToken = _accessToken,
                RefreshToken = _refreshToken,
                ExpiresAt = _expiresAt
            });
        }

        public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken)
        {
            RevokedTokens.Add(input.Token);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPayloadWriter : ICredentialPayloadWriter
    {
        private readonly StoredRow _row;

        /// <summary>Stores that landed — a compare-and-swap that found the row changed does not count.</summary>
        public int PersistCalls;

        public RecordingPayloadWriter(StoredRow row) { _row = row; }

        public Task<bool> UpdatePayloadAsync(Credential credential, string expectedEncryptedPayload, CredentialPayload newPayload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_row.StoreIfUnchanged(expectedEncryptedPayload, newPayload)) return Task.FromResult(false);

            PersistCalls++;
            return Task.FromResult(true);
        }
    }

    private sealed class IdentityEncryptor : IPayloadEncryptor
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string ciphertext) => ciphertext;
    }

    private sealed class NoopCrossProcessLock : ICrossProcessLock
    {
        public Task<IAsyncDisposable> AcquireAsync(long key, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(NoopHandle.Instance);

        private sealed class NoopHandle : IAsyncDisposable
        {
            public static readonly NoopHandle Instance = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
