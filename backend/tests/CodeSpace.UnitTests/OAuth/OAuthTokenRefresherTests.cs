using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// Pins refresh-or-not decision boundaries. The refresher must never refresh when:
/// (1) the token has no expiry, (2) no refresh_token is available, or (3) the access token
/// is still safely within its validity window. It must refresh when the access token is
/// near expiry AND a refresh_token is available.
/// </summary>
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

    private static async Task<(OAuthPayload Result, int Exchanges, int Persists)> RunAsync(OAuthPayload current, ProviderContext ctx, string? newAccessToken = null, string? newRefreshToken = null, DateTimeOffset? newExpiresAt = null)
    {
        var clock = new FixedClock(Now);
        var oauthClient = new RecordingOAuthClient(newAccessToken ?? "fresh", newRefreshToken, newExpiresAt);
        var registry = new OAuthClientRegistry(new IOAuthClient[] { oauthClient });
        var writer = new RecordingPayloadWriter();
        var resolver = new FixedResolver(current);
        var encryptor = new IdentityEncryptor();

        var refresher = new OAuthTokenRefresher(registry, writer, resolver, encryptor, clock, new NoopCrossProcessLock());

        var result = await refresher.RefreshIfNeededAsync(ctx, current, CancellationToken.None);

        return (result, oauthClient.RefreshCalls, writer.PersistCalls);
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingOAuthClient : IOAuthClient
    {
        private readonly string _accessToken;
        private readonly string? _refreshToken;
        private readonly DateTimeOffset? _expiresAt;

        public int RefreshCalls;

        public RecordingOAuthClient(string accessToken, string? refreshToken, DateTimeOffset? expiresAt)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _expiresAt = expiresAt;
        }

        public ProviderKind Kind => ProviderKind.GitLab;

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.FromResult(new OAuthTokenResponse
            {
                AccessToken = _accessToken,
                RefreshToken = _refreshToken,
                ExpiresAt = _expiresAt
            });
        }

        public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingPayloadWriter : ICredentialPayloadWriter
    {
        public int PersistCalls;

        public Task UpdatePayloadAsync(Credential credential, CredentialPayload newPayload, CancellationToken cancellationToken)
        {
            PersistCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedResolver : ICredentialResolver
    {
        private readonly OAuthPayload _payload;
        public FixedResolver(OAuthPayload payload) { _payload = payload; }
        public CredentialPayload Resolve(Credential credential) => _payload;
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
