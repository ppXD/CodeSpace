using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — GitLab refuses an access token CodeSpace has just read from the credential row, and the call is sent
/// again with the token that replaces it. The real GitLab provider (NGitLab on <c>HttpWebRequest</c>) → auth resolver → OAuth
/// strategy → refresher → Postgres advisory lock → the real OAuth client → payload writer, over real Postgres; the one fake is
/// GitLab, a <see cref="GitLabDoorkeeperStub"/> that retires an access token with its row the moment the pair is refreshed,
/// however much of its life is left, and refuses an access token past the lifetime GitLab gave it however much of it the
/// row says is left.
///
/// <para>The two shapes that reach a 401: another consumer refreshed the pair and stored it, so the entity a caller loaded
/// before still carries a token GitLab retired (what is stored is adopted, nothing is rotated); and the access token's
/// lifetime at GitLab is shorter than the row says (the stored token is the one refused, and the refresh token is live, so
/// the pair is rotated once). Before, either was a 422 for up to two hours.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class UnauthorizedRecoveryFlowTests
{
    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public UnauthorizedRecoveryFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task A_token_another_consumer_replaced_is_adopted_from_the_row_and_nothing_is_rotated()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var repo = await CredentialRefreshHarness.LoadUntrackedAsync(scope, seed.RepositoryId).ConfigureAwait(false);

        // Another pod refreshes the pair and stores it. This caller's entity, loaded before, still holds at-0, which GitLab has retired.
        var rotated = await RefreshBehindTheEntityAsync(gitlab, "rt-0").ConfigureAwait(false);
        await StoreAsync(seed.CredentialId, rotated).ConfigureAwait(false);

        var branches = await ListBranchesAsync(scope, repo).ConfigureAwait(false);

        Dump(gitlab);
        branches.ShouldBeEmpty("the call was answered");
        gitlab.Count(StubEvent.Api401).ShouldBe(1, "the stale token was refused once");
        gitlab.Count(StubEvent.Api200).ShouldBe(1, "and the call was sent again with the token the other consumer stored");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "only the other consumer's refresh: adopting needs none");
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "the row is as the other consumer left it");
    }

    [Fact]
    public async Task A_token_gitlab_retired_before_its_reported_lifetime_is_rotated_once_and_the_call_is_sent_again()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var seed = await SeedTokenGitLabRetiresAfterOneSecondAsync(gitlab).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var repo = await CredentialRefreshHarness.LoadUntrackedAsync(scope, seed.RepositoryId).ConfigureAwait(false);

        await ListBranchesAsync(scope, repo).ConfigureAwait(false);

        Dump(gitlab);
        gitlab.Count(StubEvent.Api401).ShouldBe(1);
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "the refresh token was live, so the pair was rotated once");
        gitlab.Count(StubEvent.Api200).ShouldBe(1);
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2", "the pair GitLab issued is the credential's now");
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task A_burst_of_calls_refused_together_rotates_the_pair_once_and_every_call_is_answered()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var seed = await SeedTokenGitLabRetiresAfterOneSecondAsync(gitlab).ConfigureAwait(false);

        var calls = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CallAsync(seed.RepositoryId))).ConfigureAwait(false);

        Dump(gitlab);
        calls.ShouldAllBe(failure => failure == null, $"every call is answered — {string.Join("; ", calls.Where(c => c != null).Select(c => c!.Message))}");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "one rotation for the whole burst: whoever got the lock rotated, and the rest adopted what it stored");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"a call presented a refresh token GitLab had already consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
        gitlab.Count(StubEvent.Api401).ShouldBeGreaterThan(0, "fixture check: the burst did meet the retired token");
    }

    [Fact]
    public async Task A_token_rotated_moments_ago_that_is_refused_too_is_not_rotated_again_and_the_401_is_the_answer()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var seed = await SeedTokenGitLabRetiresAfterOneSecondAsync(gitlab).ConfigureAwait(false);
        (await CallAsync(seed.RepositoryId).ConfigureAwait(false)).ShouldBeNull("fixture check: the first call recovered by rotating");

        await RevokeAtGitLabAsync(gitlab, "at-2").ConfigureAwait(false);

        var failure = await CallAsync(seed.RepositoryId).ConfigureAwait(false);

        Dump(gitlab);
        failure.ShouldBeOfType<ProviderApiException>("what GitLab said stands").StatusCode.ShouldBe(401);
        gitlab.Count(StubEvent.Rotated).ShouldBe(1, "a token issued moments ago that is refused is not aged out: rotating again would only spend a refresh token on another cause");
        gitlab.Count(StubEvent.Refused).ShouldBe(0);
        (await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active, "a 401 alone is not a verdict on the grant");
    }

    [Fact]
    public async Task A_pair_gitlab_has_revoked_behind_a_token_that_looks_live_is_a_typed_failure_and_the_credential_is_marked()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false).ConfigureAwait(false);
        await RevokeAtGitLabAsync(gitlab, "at-0").ConfigureAwait(false);

        var failure = await CallAsync(seed.RepositoryId).ConfigureAwait(false);

        Dump(gitlab);
        failure.ShouldBeOfType<CredentialNeedsReconnectException>("the 401 led to a rotation GitLab refused for good, which is what the caller is told instead of the 401").CredentialId.ShouldBe(seed.CredentialId);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Error);
        row.LastError.ShouldBe("needs reconnect: invalid_grant");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>A credential whose row says its token lives two hours, holding a pair GitLab gave a one-second lifetime — a second later GitLab refuses the token, and its refresh token is live.</summary>
    private async Task<SeededGitLabCredential> SeedTokenGitLabRetiresAfterOneSecondAsync(GitLabDoorkeeperStub gitlab)
    {
        gitlab.ExpiresInSeconds = 1;
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        gitlab.ExpiresInSeconds = 7200;

        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0", expired: false).ConfigureAwait(false);

        await Task.Delay(TimeSpan.FromMilliseconds(1300)).ConfigureAwait(false);

        return seed;
    }

    /// <summary>One call a run makes of the provider, in a scope of its own: the repository loaded untracked, then <c>ListBranchesAsync</c>. Null when it was answered, the failure when it was not.</summary>
    private async Task<Exception?> CallAsync(Guid repositoryId)
    {
        try
        {
            using var scope = _fixture.BeginScope();

            await ListBranchesAsync(scope, await CredentialRefreshHarness.LoadUntrackedAsync(scope, repositoryId).ConfigureAwait(false)).ConfigureAwait(false);

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<IReadOnlyList<CodeSpace.Messages.Dtos.Providers.RemoteBranch>> ListBranchesAsync(ILifetimeScope scope, Repository repo)
    {
        var source = scope.Resolve<IProviderRegistry>().Require<IRepositorySourceCapability>(ProviderKind.GitLab);

        return await source.ListBranchesAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), repo.ToRemoteRepository(), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>What another consumer does: presents the refresh token to GitLab directly and gets the new pair.</summary>
    private static async Task<OAuthPayload> RefreshBehindTheEntityAsync(GitLabDoorkeeperStub gitlab, string refreshToken)
    {
        using var http = new HttpClient();
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["client_id"] = GitLabDoorkeeperStub.DefaultClientId, ["client_secret"] = GitLabDoorkeeperStub.DefaultClientSecret, ["refresh_token"] = refreshToken };
        using var response = await http.PostAsync($"{gitlab.BaseUrl}/oauth/token", new FormUrlEncodedContent(form)).ConfigureAwait(false);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return new OAuthPayload { AccessToken = json.RootElement.GetProperty("access_token").GetString()!, RefreshToken = json.RootElement.GetProperty("refresh_token").GetString(), ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) };
    }

    private static async Task RevokeAtGitLabAsync(GitLabDoorkeeperStub gitlab, string token)
    {
        using var http = new HttpClient();

        await http.PostAsync($"{gitlab.BaseUrl}/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token })).ConfigureAwait(false);
    }

    /// <summary>Stores a pair into the row as another pod's refresher would.</summary>
    private async Task StoreAsync(Guid credentialId, OAuthPayload payload)
    {
        using var scope = _fixture.BeginScope();
        var sealedPair = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(payload));

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET encrypted_payload = {sealedPair}, expires_date = {payload.ExpiresAt} WHERE id = {credentialId}").ConfigureAwait(false);
    }

    private void Dump(GitLabDoorkeeperStub gitlab)
    {
        _output.WriteLine($"GitLab saw: rotated={gitlab.Count(StubEvent.Rotated)} refused={gitlab.Count(StubEvent.Refused)} api-200={gitlab.Count(StubEvent.Api200)} api-401={gitlab.Count(StubEvent.Api401)}");

        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }
}
