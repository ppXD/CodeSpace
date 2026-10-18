using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — an OAuth grant GitLab refuses for good, from the token request to the credential row. The real auth
/// resolver → OAuth strategy → refresher → Postgres advisory lock → the real <c>GitLabOAuthClient</c> → health writer, over
/// real Postgres; the one fake is GitLab, a <see cref="GitLabDoorkeeperStub"/> whose refresh tokens are single use and which
/// refuses a refresh token it never issued, or one presented by another OAuth application, as Doorkeeper does.
///
/// <para>The shape this pins is the invisible one: the stored pair is dead, every run presents it and is refused with the
/// provider's <c>invalid_grant</c>, the credential stays Active and nothing says why — and a run that fails with a string
/// nobody can link to anything. The refusal is now the provider's verdict: the credential is marked with it, the failure is
/// typed (<c>credential_needs_reconnect</c>, naming the credential), and the next use does not ask GitLab again.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DeadGrantFlowTests
{
    private const string NeedsReconnect = "credential_needs_reconnect";

    private readonly PostgresFixture _fixture;
    private readonly ITestOutputHelper _output;

    public DeadGrantFlowTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task B2_a_dead_refresh_token_is_a_typed_failure_the_credential_is_marked_and_the_next_use_does_not_ask_gitlab_again()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        // GitLab never issued this pair — the shape of a pair that was rotated away and never stored.
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-dead", "rt-dead").ConfigureAwait(false);

        var first = await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        Dump(gitlab);
        ((IFailure)first).Code.ShouldBe(NeedsReconnect, $"a refused grant is told apart from a provider fault — {first.GetType().Name}: {first.Message}");
        first.Message.ShouldContain(seed.CredentialId.ToString(), customMessage: "an operator reading a run's error must be able to tell which credential it is");
        first.Message.ShouldContain("GitLab · NACHO.L");

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Error, "the row says what the provider said, instead of staying Active with nothing to show");
        row.LastError.ShouldBe("needs reconnect: invalid_grant");

        var second = await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        ((IFailure)second).Code.ShouldBe(NeedsReconnect);
        gitlab.Count(StubEvent.Refused).ShouldBe(1, "the second use failed from what the row says; presenting the dead refresh token again would only earn the same refusal");
    }

    [Theory]
    [InlineData("app2", "secret2", "invalid_grant")]      // the instance's OAuth client id was changed: the grant belongs to the old application
    [InlineData("app1", "not-the-secret", "invalid_client")]   // the secret was changed: the client no longer authenticates
    public async Task S8_an_edited_oauth_client_is_classified_as_what_the_provider_called_it(string clientId, string clientSecret, string oauthError)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddApplication("app2", "secret2");
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        await EditInstanceClientAsync(seed.InstanceId, clientId, clientSecret).ConfigureAwait(false);

        var failure = await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        Dump(gitlab);
        ((IFailure)failure).Code.ShouldBe(NeedsReconnect, $"{failure.GetType().Name}: {failure.Message}");

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Error);
        row.LastError.ShouldBe($"needs reconnect: {oauthError}");
        gitlab.IsRefreshTokenLive("rt-0").ShouldBeTrue("the refused attempts consumed nothing: the grant itself is intact, which is what lets a reverted edit heal it");
    }

    [Fact]
    public async Task S8_a_reverted_client_edit_heals_the_credential_on_its_next_use_once_the_recheck_window_has_passed()
    {
        // The admin put the client id back. Nothing about the grant was ever wrong — GitLab consumed nothing — so the credential
        // must not stay "needs reconnect" until the sweep's next pass: a run that finds the window passed asks again, and the
        // refresh GitLab accepts lifts the verdict. Inside the window it still fails from what the row says.
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.AddApplication("app2", "secret2");
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        await EditInstanceClientAsync(seed.InstanceId, "app2", "secret2").ConfigureAwait(false);
        await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        await EditInstanceClientAsync(seed.InstanceId, GitLabDoorkeeperStub.DefaultClientId, GitLabDoorkeeperStub.DefaultClientSecret).ConfigureAwait(false);

        var inside = await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        ((IFailure)inside).Code.ShouldBe(NeedsReconnect, "marked a moment ago: not asked again yet");
        gitlab.Count(StubEvent.Refused).ShouldBe(1, "fixture check: GitLab was asked once, by the first use");

        await GitLabCredentialSeed.SetLastModifiedAsync(_fixture, seed.CredentialId, DateTimeOffset.UtcNow.AddHours(-2)).ConfigureAwait(false);

        var token = await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId).ConfigureAwait(false);

        Dump(gitlab);
        token.ShouldBe("at-2", "the use after the window asked GitLab, which accepted the grant");

        var healed = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        healed.Status.ShouldBe(CredentialStatus.Active, "the pair GitLab issued lifts the verdict");
        healed.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task A_credential_still_refused_after_the_window_costs_one_refused_call_and_waits_another_window()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-dead", "rt-dead").ConfigureAwait(false);

        await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);
        await GitLabCredentialSeed.SetLastModifiedAsync(_fixture, seed.CredentialId, DateTimeOffset.UtcNow.AddHours(-2)).ConfigureAwait(false);

        var afterTheWindow = await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        ((IFailure)afterTheWindow).Code.ShouldBe(NeedsReconnect, "still refused: the same typed failure, not a provider fault");
        gitlab.Count(StubEvent.Refused).ShouldBe(2, "the use after the window asked GitLab once more");

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Error);
        row.LastError.ShouldBe("needs reconnect: invalid_grant");
        row.LastModifiedDate.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-2), "the confirmation dates the verdict afresh, which is what the next window is measured from");

        await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        gitlab.Count(StubEvent.Refused).ShouldBe(2, "inside the new window it fails from what the row says");
    }

    [Fact]
    public async Task A_refresh_that_fails_without_a_verdict_leaves_the_credential_as_it_was()
    {
        // GitLab is down: no OAuth answer at all, so nothing is known about the grant and nothing is recorded about it.
        var gitlab = new GitLabDoorkeeperStub();
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);
        gitlab.Dispose();

        await Should.ThrowAsync<HttpRequestException>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task A_credential_disconnected_while_it_was_being_refused_is_not_marked()
    {
        // The verdict is written against the row it was judged on: a Disconnect that blanked the row meanwhile is not a
        // credential to put an Error on.
        using var gitlab = new GitLabDoorkeeperStub();
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-dead", "rt-dead").ConfigureAwait(false);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = 'Revoked', encrypted_payload = '' WHERE id = {seed.CredentialId}").ConfigureAwait(false);

        var failure = await Should.ThrowAsync<Exception>(() => CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId)).ConfigureAwait(false);

        ((IFailure)failure).Code.ShouldBe(FailureCodes.CredentialDisconnected);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Revoked);
        row.LastError.ShouldBeNull();
    }

    private async Task EditInstanceClientAsync(Guid instanceId, string clientId, string clientSecret)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = await db.ProviderInstance.SingleAsync(i => i.Id == instanceId).ConfigureAwait(false);

        instance.OauthClientId = clientId;
        instance.OauthClientSecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt(clientSecret);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private void Dump(GitLabDoorkeeperStub gitlab)
    {
        _output.WriteLine($"GitLab saw: rotated={gitlab.Count(StubEvent.Rotated)} refused={gitlab.Count(StubEvent.Refused)}");

        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }
}
