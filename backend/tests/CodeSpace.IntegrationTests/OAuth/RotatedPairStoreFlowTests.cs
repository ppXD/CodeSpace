using System.Collections;
using System.Reflection;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// 🟢 High fidelity: a refresh token GitLab has rotated is gone for good, so the pair it issued in exchange must reach the
/// credential row whatever happens between the answer and the store — a store that fails, a row someone wrote over. Real
/// production classes end to end (auth resolver → OAuth strategy → refresher → Postgres advisory lock → the real
/// <see cref="GitLabOAuthClient"/> → payload writer) over real Postgres; the only fake is GitLab, a
/// <see cref="GitLabDoorkeeperStub"/> on a loopback port that refuses a refresh token the second time it is presented.
///
/// <para>The shape this pins: the exchange succeeds, GitLab has consumed rt-0, and storing rt-2 fails. The row still holds
/// rt-0, every later run presents it and is refused with invalid_grant, the credential stays Active and nothing says why.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RotatedPairStoreFlowTests
{
    private readonly PostgresFixture _fixture;

    public RotatedPairStoreFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_pair_no_store_could_keep_is_stored_by_the_next_run_instead_of_presenting_the_consumed_token_again()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        // Every store the first run tries fails — whatever the refresher's retry bound is.
        await Should.ThrowAsync<NpgsqlException>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId, writer => new FailingCredentialPayloadWriter(writer, int.MaxValue))).ConfigureAwait(false);

        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "fixture check: GitLab rotated the pair before the store failed");

        var token = await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId).ConfigureAwait(false);

        token.ShouldBe("at-2", "the next run should use the pair the first one was issued");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"the next run presented a refresh token GitLab had already consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "storing the kept pair needs no second refresh");
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "the row must hold the pair GitLab is waiting for");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task A_store_that_fails_once_is_retried_and_costs_the_run_nothing()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);
        FailingCredentialPayloadWriter? writer = null;

        var token = await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId, inner => writer = new FailingCredentialPayloadWriter(inner, failures: 1)).ConfigureAwait(false);

        token.ShouldBe("at-2");
        writer!.Attempts.ShouldBe(2, "one failed store, one retry that landed");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1);
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2");
    }

    [Fact]
    public async Task A_stale_payload_written_into_the_row_while_gitlab_answers_does_not_cost_the_rotated_pair()
    {
        // The row changes between the refresher's read and its store — here a copy of the pair GitLab is rotating away, put
        // back by a unit of work that loaded the credential before the rotation and saved after it. The row is not empty, so
        // it is not a revocation, and what it holds is the consumed rt-0: dropping the pair just issued for it leaves the
        // credential holding a token no later run can use.
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        var token = await ResolveWhileSomeoneRestoresTheOldPairAsync(seed).ConfigureAwait(false);

        token.ShouldBe("at-2", "the run should use the pair GitLab issued it, not the consumed one it found in the row");
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "the stale copy must not stay the credential's pair");
        (await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId).ConfigureAwait(false)).ShouldBe("at-2");
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
    }

    [Fact]
    public async Task A_read_that_fails_right_after_the_swap_was_lost_does_not_cost_the_rotated_pair()
    {
        // The same stale payload as above, and then the refresher's re-read of the row fails (a dropped connection). GitLab
        // has rotated rt-0 away, the row holds the stale copy of it, and the pair just issued is in no store at all.
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        await Should.ThrowAsync<NpgsqlException>(() => ResolveWhileSomeoneRestoresTheOldPairAsync(seed, failTheNextRead: true)).ConfigureAwait(false);

        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "fixture check: GitLab rotated the pair before the read failed");
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-0", "fixture check: the row holds the stale copy, so nothing has stored the pair");

        var token = await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId).ConfigureAwait(false);

        token.ShouldBe("at-2", "the next run should use the pair the first one was issued");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"the next run presented a refresh token GitLab had already consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "storing the kept pair needs no second refresh");
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2");
    }

    [Fact]
    public async Task A_second_pods_refusal_of_the_consumed_pair_does_not_strand_the_pair_the_first_pod_kept()
    {
        // Pod A: GitLab rotates rt-0 and every store fails, so A keeps rt-2 in memory. Pod B has no such memory: it reads the row
        // — still the consumed rt-0 — and is refused for good, which marks the credential Error. The credential is not dead: a
        // live pair exists, in A. A's next use must store it, not be told to reconnect by the verdict B's refusal wrote.
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        await Should.ThrowAsync<NpgsqlException>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId, writer => new FailingCredentialPayloadWriter(writer, int.MaxValue))).ConfigureAwait(false);

        using var podB = KeptPairs.SetAsideLikeAnotherProcess(seed.CredentialId);

        await Should.ThrowAsync<CredentialNeedsReconnectException>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        var verdict = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        verdict.Status.ShouldBe(CredentialStatus.Error, "fixture check: pod B's refusal marked the credential");
        gitlab.Count(StubEvent.Refused).ShouldBe(1, "fixture check: B presented the consumed refresh token and was refused");

        podB.PodAIsStillRunning();

        var token = await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId).ConfigureAwait(false);

        token.ShouldBe("at-2", "pod A holds the pair GitLab is waiting for, and its next use stores it before anything judges the credential by the row");
        gitlab.Count(StubEvent.Refused).ShouldBe(1, "A presented nothing GitLab had consumed");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "storing the kept pair needs no second refresh");
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "the row holds the pair GitLab is waiting for");

        var healed = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        healed.Status.ShouldBe(CredentialStatus.Active, "a live pair stored over the verdict lifts it");
        healed.LastError.ShouldBeNull();
        KeptPairs.IsHeldFor(seed.CredentialId).ShouldBeFalse("stored, so no longer kept");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>One run whose refresh is answered by GitLab and then, before the refresher stores, finds the old pair back in the row.</summary>
    private async Task<string> ResolveWhileSomeoneRestoresTheOldPairAsync(SeededGitLabCredential seed, bool failTheNextRead = false)
    {
        using var scope = _fixture.BeginScope();
        var real = scope.Resolve<IOAuthClientRegistry>().Get(ProviderKind.GitLab);
        var reader = new FailingOnceCredentialPayloadReader(scope.Resolve<ICredentialPayloadReader>());

        using var run = scope.BeginLifetimeScope(b =>
        {
            b.RegisterInstance<IOAuthClientRegistry>(new SingleClientRegistry(new RestoringOAuthClient(real, async () =>
            {
                await RestoreOldPairAsync(seed.CredentialId).ConfigureAwait(false);

                if (failTheNextRead) reader.FailNextRead();
            })));
            b.RegisterInstance<ICredentialPayloadReader>(reader);
        });

        var repo = await CredentialRefreshHarness.LoadUntrackedAsync(run, seed.RepositoryId).ConfigureAwait(false);
        var auth = await run.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), CancellationToken.None).ConfigureAwait(false);

        return auth.Token;
    }

    private async Task RestoreOldPairAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var old = new OAuthPayload { AccessToken = "at-0", RefreshToken = "rt-0", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var sealedOld = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(old));

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET encrypted_payload = {sealedOld} WHERE id = {credentialId}").ConfigureAwait(false);
    }

    /// <summary>The real reader; once told, its next read throws as a dropped connection does, and the ones after it go through.</summary>
    private sealed class FailingOnceCredentialPayloadReader : ICredentialPayloadReader
    {
        private readonly ICredentialPayloadReader _inner;
        private bool _failNext;

        public FailingOnceCredentialPayloadReader(ICredentialPayloadReader inner) { _inner = inner; }

        public void FailNextRead() => _failNext = true;

        public Task<string> ReadEncryptedPayloadAsync(Guid credentialId, CancellationToken cancellationToken)
        {
            if (!_failNext) return _inner.ReadEncryptedPayloadAsync(credentialId, cancellationToken);

            _failNext = false;

            throw new NpgsqlException("simulated: the connection was terminated while re-reading the row");
        }

        public Task<bool> IsRetiredAsync(Guid credentialId, CancellationToken cancellationToken) => _inner.IsRetiredAsync(credentialId, cancellationToken);
    }

    private sealed class SingleClientRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;

        public SingleClientRegistry(IOAuthClient client) { _client = client; }

        public IOAuthClient Get(ProviderKind kind) => _client;
    }

    /// <summary>The real client; once GitLab has answered a refresh, <c>afterRefresh</c> runs before the answer reaches the refresher.</summary>
    private sealed class RestoringOAuthClient : IOAuthClient
    {
        private readonly IOAuthClient _inner;
        private readonly Func<Task> _afterRefresh;

        public RestoringOAuthClient(IOAuthClient inner, Func<Task> afterRefresh)
        {
            _inner = inner;
            _afterRefresh = afterRefresh;
        }

        public ProviderKind Kind => _inner.Kind;

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => _inner.BuildAuthorizeUrl(input);

        public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken) => _inner.ExchangeCodeAsync(input, cancellationToken);

        public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken) => _inner.RevokeAsync(input, cancellationToken);

        public async Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken)
        {
            var answer = await _inner.RefreshAsync(input, cancellationToken).ConfigureAwait(false);

            await _afterRefresh().ConfigureAwait(false);

            return answer;
        }
    }

    /// <summary>
    /// The refresher keeps a pair it could not store in a static, per process, so a second pod "has no such memory". Tests run
    /// every pod in one process, so the second pod is made by taking the pair out of that static for the time it runs and
    /// putting it back when the first pod is alive again. Reflection, because the memory is deliberately not part of the
    /// refresher's surface.
    /// </summary>
    private sealed class KeptPairs : IDisposable
    {
        private static readonly IDictionary Memory = (IDictionary)typeof(OAuthTokenRefresher).GetField("KeptRotations", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        private readonly Guid _credentialId;
        private object? _setAside;

        private KeptPairs(Guid credentialId, object setAside)
        {
            _credentialId = credentialId;
            _setAside = setAside;
        }

        public static KeptPairs SetAsideLikeAnotherProcess(Guid credentialId)
        {
            var kept = Memory[credentialId] ?? throw new ShouldAssertException($"fixture check: no process kept a pair for credential {credentialId}");

            Memory.Remove(credentialId);

            return new KeptPairs(credentialId, kept);
        }

        public static bool IsHeldFor(Guid credentialId) => Memory.Contains(credentialId);

        public void PodAIsStillRunning()
        {
            Memory[_credentialId] = _setAside;
            _setAside = null;
        }

        /// <summary>A test that fails before pod A is back must not leave its pair in the static for the next test of this process.</summary>
        public void Dispose()
        {
            if (_setAside is null) return;

            Memory.Remove(_credentialId);
        }
    }
}
