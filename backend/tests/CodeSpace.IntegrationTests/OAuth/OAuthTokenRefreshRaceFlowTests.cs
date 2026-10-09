using System.Text.Json;
using System.Web;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// GitLab rotates the refresh token on every refresh and refuses a used one with <c>invalid_grant</c>. Runs that find
/// the same credential's access token expired at once — map branches, sibling runs, pods of a multi-node deployment —
/// each loaded the credential row, with the soon-to-be-consumed refresh token, before the first refresh finished. They
/// must refresh it ONCE and all use the token that refresh stored. Once GitLab has consumed the old token, the rotated
/// pair must be stored even if the run is cancelled; and it must never land in a row a revocation emptied meanwhile.
///
/// <para>Drives the real chain: workspace / auth resolver → GitLab OAuth strategy → refresher → Postgres advisory lock
/// → the real <see cref="GitLabOAuthClient"/> against a loopback token endpoint that rotates like GitLab → the detached
/// payload writer. Every caller works in its own DI scope with its own DbContext, as separate runs and pods do.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OAuthTokenRefreshRaceFlowTests
{
    private const int ConcurrentRuns = 4;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly PostgresFixture _fixture;

    public OAuthTokenRefreshRaceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Concurrent_runs_with_an_expired_token_refresh_it_once()
    {
        using var provider = new StubProviderHost();
        var endpoint = RotatingTokenEndpoint.Serve(provider, liveRefreshToken: "rt-0");
        var seed = await SeedExpiredOAuthRepositoryAsync(provider.BaseUrl, refreshToken: "rt-0").ConfigureAwait(false);

        var loaded = Enumerable.Range(0, ConcurrentRuns).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();

        async Task<string> RunAsync(int run)
        {
            using var scope = _fixture.BeginScope();
            var repo = await LoadRepositoryAsync(scope, seed.RepositoryId).ConfigureAwait(false);

            // Every run holds the expired row in memory before any of them refreshes — the window the race needs.
            loaded[run].SetResult();
            await Task.WhenAll(loaded.Select(l => l.Task)).ConfigureAwait(false);

            var auth = await scope.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), CancellationToken.None).ConfigureAwait(false);

            return auth.Token;
        }

        var tokens = await WithinPatienceAsync(Task.WhenAll(Enumerable.Range(0, ConcurrentRuns).Select(RunAsync)), $"the {ConcurrentRuns} runs did not finish", seed.CredentialId).ConfigureAwait(false);

        tokens.ShouldAllBe(token => token == "at-1", customMessage: $"every run should use the one refreshed access token; got [{string.Join(", ", tokens)}]");
        endpoint.Requests.ShouldBe(1, customMessage: "the token endpoint should see exactly one refresh for the credential — a second one presents the refresh token the first consumed");

        var stored = await ReadStoredPayloadAsync(seed.CredentialId).ConfigureAwait(false);
        stored.RefreshToken.ShouldBe("rt-1", customMessage: "the rotated refresh token must be what the row holds, or the next refresh is refused");
    }

    [Fact]
    public async Task A_run_waiting_on_another_pods_refresh_clones_with_the_token_it_stored()
    {
        using var provider = new StubProviderHost();
        var endpoint = RotatingTokenEndpoint.Serve(provider, liveRefreshToken: "rt-0");
        var seed = await SeedExpiredOAuthRepositoryAsync(provider.BaseUrl, refreshToken: "rt-0").ConfigureAwait(false);

        // The other pod is mid-refresh: it holds the credential's refresh lock.
        await using var otherPod = await NpgsqlRefreshLock.AcquireAsync(_fixture.ConnectionString, seed.CredentialId).ConfigureAwait(false);

        var run = Task.Run(async () =>
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(seed.RepositoryId, seed.TeamId, CancellationToken.None).ConfigureAwait(false);
        });

        // Once the run queues behind the lock it has loaded the row — with the refresh token the other pod is consuming.
        var queued = otherPod.WaitForWaiterAsync(Patience);

        if (await Task.WhenAny(run, queued).ConfigureAwait(false) == run) await run.ConfigureAwait(false);   // surfaces why it never queued

        await queued.ConfigureAwait(false);

        await RefreshAsOtherPodAsync(seed.CredentialId).ConfigureAwait(false);
        await otherPod.DisposeAsync().ConfigureAwait(false);

        var clone = await WithinPatienceAsync(run, "the run did not finish after the other pod released the lock", seed.CredentialId).ConfigureAwait(false);

        clone.ShouldNotBeNull();
        clone.Token.ShouldBe("at-1", customMessage: "the run should clone with the access token the other pod stored, not refresh again");
        endpoint.Requests.ShouldBe(1, customMessage: "only the other pod's refresh should reach the token endpoint");
    }

    [Fact]
    public async Task A_refresh_refused_after_its_lock_was_lost_uses_the_token_the_new_holder_stored()
    {
        // Nothing stores a token without holding the refresh lock, so a refused refresh finds a newer token stored only
        // when its own lock session ended mid-refresh (a killed connection, idle_session_timeout, a failover): the pod
        // queued behind it took the lock, refreshed rt-0 first — GitLab already answered it, so rt-1 is live — and
        // stores the rotated pair while this run's refresh is refused.
        using var provider = new StubProviderHost();
        var seed = await SeedExpiredOAuthRepositoryAsync(provider.BaseUrl, refreshToken: "rt-0").ConfigureAwait(false);
        var otherPodsPair = new OAuthPayload { AccessToken = "at-1", RefreshToken = "rt-1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) };
        var endpoint = RotatingTokenEndpoint.Serve(provider, liveRefreshToken: "rt-1", onRefused: () => LoseRefreshLockToAnotherPodAsync(seed.CredentialId, otherPodsPair).GetAwaiter().GetResult());

        var token = await ResolveTokenAsync(seed.RepositoryId).ConfigureAwait(false);

        token.ShouldBe("at-1", customMessage: "a refusal after the pod that took the lock over stored a newer token should use that token rather than fail the run");
        endpoint.Requests.ShouldBe(1, customMessage: "the refused refresh is not retried against the provider");
    }

    [Fact]
    public async Task A_refresh_refused_because_the_credential_was_revoked_says_it_was_revoked()
    {
        // Revocation does not take the refresh lock. The user disconnects while this run refreshes: the revocation already
        // killed rt-0 at GitLab, so the refresh is refused, and it empties the row while the refusal is answered.
        using var provider = new StubProviderHost();
        var seed = await SeedExpiredOAuthRepositoryAsync(provider.BaseUrl, refreshToken: "rt-0").ConfigureAwait(false);
        var endpoint = RotatingTokenEndpoint.Serve(provider, liveRefreshToken: null, onRefused: () => RevokeAsUserAsync(seed).GetAwaiter().GetResult());

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => ResolveTokenAsync(seed.RepositoryId)).ConfigureAwait(false);

        failure.Message.ShouldContain("was revoked", customMessage: "a run whose credential was disconnected mid-refresh should be told to reconnect, not shown GitLab's invalid_grant");
        endpoint.Requests.ShouldBe(1, customMessage: "a revoked credential is not refreshed again");
    }

    [Fact]
    public async Task A_credential_revoked_while_the_provider_rotated_its_token_stays_revoked()
    {
        // Revocation does not take the refresh lock: the user disconnects while GitLab answers this run's refresh, so the
        // revocation reaches GitLab with rt-0 and the pair GitLab just issued is live and held by nothing but this run.
        using var provider = new StubProviderHost();
        var seed = await SeedExpiredOAuthRepositoryAsync(provider.BaseUrl, refreshToken: "rt-0").ConfigureAwait(false);
        var endpoint = RotatingTokenEndpoint.Serve(provider, liveRefreshToken: "rt-0", onRotated: () => RevokeAsUserAsync(seed).GetAwaiter().GetResult());

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => ResolveTokenAsync(seed.RepositoryId)).ConfigureAwait(false);

        failure.Message.ShouldContain("was revoked", customMessage: "the run should not get a token for a credential the user disconnected");

        var row = await ReadCredentialAsync(seed.CredentialId).ConfigureAwait(false);
        row.Status.ShouldBe(CredentialStatus.Revoked);
        row.EncryptedPayload.ShouldBeEmpty(customMessage: "the refresh must not write the pair GitLab just issued back into a row the user disconnected");
        endpoint.IsLive("rt-1").ShouldBeFalse(customMessage: "the pair issued for a credential revoked meanwhile is stored nowhere, so it should be revoked at GitLab rather than left a live grant");
    }

    [Fact]
    public async Task A_run_cancelled_after_the_provider_rotated_its_token_leaves_the_rotated_pair_stored()
    {
        // The run is cancelled — or the API request aborted — after GitLab consumed rt-0 and before its answer arrived.
        using var provider = new StubProviderHost();
        using var cancelled = new CancellationTokenSource();
        var seed = await SeedExpiredOAuthRepositoryAsync(provider.BaseUrl, refreshToken: "rt-0").ConfigureAwait(false);
        var endpoint = RotatingTokenEndpoint.Serve(provider, liveRefreshToken: "rt-0", onRotated: cancelled.Cancel);

        try
        {
            await ResolveTokenAsync(seed.RepositoryId, cancelled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The run may still see its own cancellation. What has to survive it is the stored pair.
        }

        var stored = await ReadStoredPayloadAsync(seed.CredentialId).ConfigureAwait(false);
        stored.RefreshToken.ShouldBe("rt-1", customMessage: "GitLab consumed rt-0; unless the rotated pair is stored, every later run presents rt-0 and is refused until the user reconnects");

        var rerun = await ResolveTokenAsync(seed.RepositoryId).ConfigureAwait(false);

        rerun.ShouldBe("at-1", customMessage: "a re-run should use the pair the cancelled run stored");
        endpoint.Requests.ShouldBe(1, customMessage: "the re-run should not need a refresh of its own");
    }

    [Fact]
    public async Task A_refused_refresh_with_nothing_newer_stored_surfaces_the_providers_refusal()
    {
        using var provider = new StubProviderHost();
        var endpoint = RotatingTokenEndpoint.Serve(provider, liveRefreshToken: "rt-revoked-elsewhere");
        var seed = await SeedExpiredOAuthRepositoryAsync(provider.BaseUrl, refreshToken: "rt-0").ConfigureAwait(false);

        var refusal = await Should.ThrowAsync<OAuthExchangeException>(() => ResolveTokenAsync(seed.RepositoryId)).ConfigureAwait(false);

        refusal.Error.ShouldBe("invalid_grant");
        endpoint.Requests.ShouldBe(1, customMessage: "a refusal with nothing newer stored is final — no retry loop against the provider");

        var stored = await ReadStoredPayloadAsync(seed.CredentialId).ConfigureAwait(false);
        stored.RefreshToken.ShouldBe("rt-0", customMessage: "a refused refresh must leave the stored token alone");
    }

    // ── Plumbing ───────────────────────────────────────────────────────────────────

    private async Task<string> ResolveTokenAsync(Guid repositoryId, CancellationToken cancellationToken = default)
    {
        using var scope = _fixture.BeginScope();
        var repo = await LoadRepositoryAsync(scope, repositoryId).ConfigureAwait(false);
        var auth = await scope.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), cancellationToken).ConfigureAwait(false);

        return auth.Token;
    }

    /// <summary>Loads the repository the way the workspace resolver does: untracked, with its instance and credential.</summary>
    private static async Task<Repository> LoadRepositoryAsync(ILifetimeScope scope, Guid repositoryId) =>
        await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().Include(r => r.ProviderInstance).Include(r => r.Credential).SingleAsync(r => r.Id == repositoryId).ConfigureAwait(false);

    /// <summary>What another pod's refresher does under the lock: refresh against the provider, then store the rotated pair.</summary>
    private async Task RefreshAsOtherPodAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credential = await db.Credential.AsNoTracking().Include(c => c.ProviderInstance).SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        var current = (OAuthPayload)scope.Resolve<ICredentialResolver>().Resolve(credential);
        var instance = credential.ProviderInstance;

        var response = await scope.Resolve<IOAuthClientRegistry>().Get(ProviderKind.GitLab).RefreshAsync(new OAuthRefreshInput
        {
            Instance = instance,
            ClientId = instance.OauthClientId!,
            ClientSecret = scope.Resolve<IPayloadEncryptor>().Decrypt(instance.OauthClientSecretEnc!),
            RefreshToken = current.RefreshToken!
        }, CancellationToken.None).ConfigureAwait(false);

        var rotated = new OAuthPayload { AccessToken = response.AccessToken, RefreshToken = response.RefreshToken, ExpiresAt = response.ExpiresAt };

        var stored = await scope.Resolve<ICredentialPayloadWriter>().UpdatePayloadAsync(credential, credential.EncryptedPayload, rotated, CancellationToken.None).ConfigureAwait(false);

        stored.ShouldBeTrue(customMessage: "the other pod holds the lock, so the row should still hold what it read");
    }

    /// <summary>What a refresher holding the lock stores: the pair, over the payload it read.</summary>
    private async Task StoreAsync(Guid credentialId, OAuthPayload payload)
    {
        using var scope = _fixture.BeginScope();
        var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);

        var stored = await scope.Resolve<ICredentialPayloadWriter>().UpdatePayloadAsync(credential, credential.EncryptedPayload, payload, CancellationToken.None).ConfigureAwait(false);

        stored.ShouldBeTrue(customMessage: "the store runs under the lock, so the row should still hold what it read");
    }

    private async Task<OAuthPayload> ReadStoredPayloadAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var credential = await ReadCredentialAsync(credentialId).ConfigureAwait(false);

        return (OAuthPayload)scope.Resolve<ICredentialResolver>().Resolve(credential);
    }

    private async Task<Credential> ReadCredentialAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
    }

    /// <summary>
    /// The user disconnecting the credential: the real revoke command, as an admin of its team. Its own call to the
    /// provider's revoke endpoint goes to a recording client — the hooks that run this are answering the refresher's
    /// request to the stub, which serves one request at a time.
    /// </summary>
    private async Task RevokeAsUserAsync(SeededRepository seed)
    {
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId, Roles.Admin);
        using var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleClientRegistry(new StubOAuthClient(ProviderKind.GitLab, new OAuthTokenResponse { AccessToken = "unused" }))).SingleInstance());

        await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = seed.CredentialId }).ConfigureAwait(false);
    }

    /// <summary>
    /// The refresher's advisory-lock session ends while its refresh is at the provider, so the pod queued behind it takes
    /// the lock and stores the pair its own refresh got. Terminating the session is the real loss: the other pod's
    /// <c>pg_advisory_lock</c> only returns because of it.
    /// </summary>
    private async Task LoseRefreshLockToAnotherPodAsync(Guid credentialId, OAuthPayload otherPodsPair)
    {
        await TerminateRefreshLockSessionAsync(credentialId).ConfigureAwait(false);

        await using var otherPod = await WithinPatienceAsync(NpgsqlRefreshLock.AcquireAsync(_fixture.ConnectionString, credentialId), "the other pod could not take the refresh lock after its holder's session was terminated", credentialId).ConfigureAwait(false);

        await StoreAsync(credentialId, otherPodsPair).ConfigureAwait(false);
    }

    private async Task TerminateRefreshLockSessionAsync(Guid credentialId)
    {
        var key = OAuthTokenRefresher.CredentialIdToLockKey(credentialId);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        // pg_locks shows a bigint advisory key as its high half in classid and its low half in objid, with objsubid 1.
        await using var cmd = new NpgsqlCommand("SELECT count(*) FILTER (WHERE pg_terminate_backend(pid)) FROM pg_locks WHERE locktype = 'advisory' AND granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database()) AND classid::bigint = @high AND objid::bigint = @low AND objsubid = 1", conn);
        cmd.Parameters.AddWithValue("high", (long)(uint)(key >> 32));
        cmd.Parameters.AddWithValue("low", (long)(uint)key);

        var terminated = (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;

        terminated.ShouldBe(1, customMessage: "exactly one session — the refresher's — should hold the credential's refresh lock while its request is at the provider");
    }

    /// <summary>
    /// Bounds a wait that hangs, if it hangs, on the refresh semaphore or the advisory lock — and says how to see which.
    /// </summary>
    private static async Task<T> WithinPatienceAsync<T>(Task<T> work, string signal, Guid credentialId)
    {
        try
        {
            return await work.WaitAsync(Patience).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            throw new TimeoutException($"{signal} within {Patience.TotalSeconds}s — most likely stuck on the in-process refresh semaphore or the Postgres advisory lock (key {OAuthTokenRefresher.CredentialIdToLockKey(credentialId)}) for credential {credentialId}. While the test hangs, inspect `SELECT pid, pg_blocking_pids(pid), wait_event_type, state, query FROM pg_stat_activity WHERE datname = current_database()`.", timeout);
        }
    }

    private async Task<SeededRepository> SeedExpiredOAuthRepositoryAsync(string providerBaseUrl, string refreshToken)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = providerBaseUrl, OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret") };
        var expired = new OAuthPayload { AccessToken = "at-0", RefreshToken = refreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, AuthType = AuthType.OAuth, DisplayName = "oauth", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(expired)), ExpiresDate = expired.ExpiresAt, Status = CredentialStatus.Active };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = suffix, NamespacePath = "acme", Name = "repo", FullPath = "acme/repo", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{providerBaseUrl}/acme/repo", CloneUrlHttps = $"{providerBaseUrl}/acme/repo.git", Status = RepositoryStatus.Active };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeededRepository(user.Id, team.Id, credential.Id, repository.Id);
    }

    private sealed record SeededRepository(Guid UserId, Guid TeamId, Guid CredentialId, Guid RepositoryId);

    private sealed class SingleClientRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;

        public SingleClientRegistry(IOAuthClient client) { _client = client; }

        public IOAuthClient Get(ProviderKind kind) => _client;
    }

    /// <summary>
    /// GitLab's token endpoint for <c>grant_type=refresh_token</c>: each refresh token is single-use — presenting the live
    /// one rotates it (<c>rt-N</c> → <c>rt-N+1</c>, with access token <c>at-N+1</c>); presenting any other is refused with
    /// GitLab's own <c>invalid_grant</c> body. Its RFC 7009 revoke endpoint kills the live refresh token when that is the
    /// one presented. <c>onRotated</c> / <c>onRefused</c> run before a rotation / refusal is answered.
    /// </summary>
    private sealed class RotatingTokenEndpoint
    {
        private const string TokenPath = "/oauth/token";
        private const string RevokePath = "/oauth/revoke";
        private const string InvalidGrantBody = """{"error":"invalid_grant","error_description":"The provided authorization grant is invalid, expired, revoked, does not match the redirection URI used in the authorization request, or was issued to another client."}""";

        private readonly object _gate = new();
        private readonly StubProviderHost _provider;
        private readonly Action? _onRotated;
        private readonly Action? _onRefused;
        private string? _liveRefreshToken;

        private RotatingTokenEndpoint(StubProviderHost provider, string? liveRefreshToken, Action? onRotated, Action? onRefused)
        {
            _provider = provider;
            _liveRefreshToken = liveRefreshToken;
            _onRotated = onRotated;
            _onRefused = onRefused;
        }

        /// <summary><paramref name="liveRefreshToken"/> null: no refresh token is live — every refresh is refused.</summary>
        public static RotatingTokenEndpoint Serve(StubProviderHost provider, string? liveRefreshToken, Action? onRotated = null, Action? onRefused = null)
        {
            var endpoint = new RotatingTokenEndpoint(provider, liveRefreshToken, onRotated, onRefused);
            provider.Answer("POST", TokenPath, endpoint.Refresh).Answer("POST", RevokePath, endpoint.Revoke);
            return endpoint;
        }

        /// <summary>Refresh requests that reached the endpoint, rotated or refused.</summary>
        public int Requests => _provider.Requests.Count(r => r.PathAndQuery.StartsWith(TokenPath, StringComparison.Ordinal));

        public bool IsLive(string refreshToken)
        {
            lock (_gate) { return _liveRefreshToken == refreshToken; }
        }

        private StubProviderHost.StubReply Refresh(StubProviderHost.RecordedRequest request)
        {
            var presented = HttpUtility.ParseQueryString(request.Body)["refresh_token"];
            StubProviderHost.StubReply? rotated;

            lock (_gate)
            {
                rotated = presented != null && presented == _liveRefreshToken ? Rotate() : null;
            }

            return RunHook(rotated != null ? _onRotated : _onRefused) ?? rotated ?? new StubProviderHost.StubReply(400, InvalidGrantBody);
        }

        private StubProviderHost.StubReply Revoke(StubProviderHost.RecordedRequest request)
        {
            var token = HttpUtility.ParseQueryString(request.Body)["token"];

            lock (_gate)
            {
                if (token == _liveRefreshToken) _liveRefreshToken = null;
            }

            return new StubProviderHost.StubReply(200, "{}");
        }

        private StubProviderHost.StubReply Rotate()
        {
            var generation = int.Parse(_liveRefreshToken!["rt-".Length..]) + 1;
            _liveRefreshToken = $"rt-{generation}";

            return new StubProviderHost.StubReply(200, JsonSerializer.Serialize(new { access_token = $"at-{generation}", token_type = "Bearer", expires_in = 7200, refresh_token = _liveRefreshToken, scope = "api" }));
        }

        /// <summary>
        /// A hook that throws must not take the stub's serve loop down — the refresher would then wait out its HTTP timeout.
        /// The failure is answered instead, as an OAuth error, so it surfaces as the refresh's exception.
        /// </summary>
        private static StubProviderHost.StubReply? RunHook(Action? hook)
        {
            try
            {
                hook?.Invoke();
                return null;
            }
            catch (Exception ex)
            {
                return new StubProviderHost.StubReply(500, JsonSerializer.Serialize(new { error = "test_hook_failed", error_description = ex.ToString() }));
            }
        }
    }

    /// <summary>
    /// Another pod's hold on a credential's refresh lock: the same Postgres advisory key the refresher takes, on an
    /// unpooled connection of its own, so disposing it ends the session and releases the lock.
    /// </summary>
    private sealed class NpgsqlRefreshLock : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly string _connectionString;
        private readonly int _pid;
        private bool _released;

        private NpgsqlRefreshLock(NpgsqlConnection connection, string connectionString, int pid)
        {
            _connection = connection;
            _connectionString = connectionString;
            _pid = pid;
        }

        public static async Task<NpgsqlRefreshLock> AcquireAsync(string connectionString, Guid credentialId)
        {
            var unpooled = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
            var connection = new NpgsqlConnection(unpooled);
            await connection.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_lock(@key), pg_backend_pid()", connection);
            cmd.Parameters.AddWithValue("key", OAuthTokenRefresher.CredentialIdToLockKey(credentialId));
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            await reader.ReadAsync().ConfigureAwait(false);

            return new NpgsqlRefreshLock(connection, unpooled, reader.GetInt32(1));
        }

        /// <summary>Waits until some other backend is blocked on this lock.</summary>
        public async Task WaitForWaiterAsync(TimeSpan patience)
        {
            var deadline = DateTimeOffset.UtcNow + patience;

            while (await CountWaitersAsync().ConfigureAwait(false) == 0)
            {
                if (DateTimeOffset.UtcNow > deadline)
                    throw new TimeoutException($"No backend queued behind the refresh lock held by pid {_pid} within {patience.TotalSeconds}s — the run never reached the refresher's lock. Inspect `SELECT pid, pg_blocking_pids(pid) FROM pg_stat_activity` while the test hangs.");

                await Task.Delay(50).ConfigureAwait(false);
            }
        }

        private async Task<long> CountWaitersAsync()
        {
            await using var probe = new NpgsqlConnection(_connectionString);
            await probe.OpenAsync().ConfigureAwait(false);

            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid))", probe);
            cmd.Parameters.AddWithValue("pid", _pid);

            return (long)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
        }

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;

            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
