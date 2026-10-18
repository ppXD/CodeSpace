using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — a pair the provider just issued is proof it accepts the grant, so the payload writer that stores it
/// lifts an <c>Error</c> the credential carried, and the text that explained it. That matters when a verdict and a refresh
/// cross: a pod refused the consumed refresh token and marked the row, or the lock session was lost mid-rotation and another
/// pod judged it — and a refresh then lands. Nothing else lifts the verdict at that moment (the health sweep only looks at
/// an Error credential an hour later), so a credential holding a live pair would read "needs reconnect" until it did.
///
/// <para>Real Postgres and the real payload writer; the race is made exactly as it happens — the entity is loaded, the row is
/// marked, then the refresh runs from the entity — against a <see cref="GitLabDoorkeeperStub"/> that honours the pair.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RefreshLiftsAnErrorFlowTests
{
    private const string Reason = "needs reconnect: invalid_grant";

    private readonly PostgresFixture _fixture;

    public RefreshLiftsAnErrorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_refresh_the_provider_accepts_lifts_the_Error_a_racing_pod_marked_after_this_one_loaded_the_credential()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var repo = await CredentialRefreshHarness.LoadUntrackedAsync(scope, seed.RepositoryId).ConfigureAwait(false);

        await MarkErrorAsync(seed.CredentialId, Reason).ConfigureAwait(false);

        var auth = await scope.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), CancellationToken.None).ConfigureAwait(false);

        auth.Token.ShouldBe("at-2", "the entity was loaded before the verdict, so it presents the pair the provider still honours");
        gitlab.Count(StubEvent.Rotated).ShouldBe(1);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Active, "the provider accepted the grant, so the verdict is lifted with the pair it issued — not left for the sweep an hour later");
        row.LastError.ShouldBeNull();
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-2");
    }

    [Theory]
    [InlineData(CredentialStatus.Error, CredentialStatus.Active, null)]
    [InlineData(CredentialStatus.Active, CredentialStatus.Active, null)]
    [InlineData(CredentialStatus.Expired, CredentialStatus.Expired, "kept as it was")]
    public async Task The_payload_writer_lifts_an_Error_with_its_text_and_leaves_any_other_status_as_it_was(CredentialStatus before, CredentialStatus after, string? lastErrorAfter)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        await SetStatusAsync(seed.CredentialId, before, before == CredentialStatus.Active ? null : before == CredentialStatus.Error ? Reason : "kept as it was").ConfigureAwait(false);

        var loaded = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);
        var issued = new OAuthPayload { AccessToken = "at-new", RefreshToken = "rt-new", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) };

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<ICredentialPayloadWriter>().UpdatePayloadAsync(loaded, loaded.EncryptedPayload, issued, CancellationToken.None).ConfigureAwait(false)).ShouldBeTrue();

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(after);
        row.LastError.ShouldBe(lastErrorAfter);
        loaded.Status.ShouldBe(after, "the caller's entity says what the row now holds, so a later read in its scope does not fail fast on a verdict that is gone");
        loaded.LastError.ShouldBe(lastErrorAfter);
        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-new");
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private Task MarkErrorAsync(Guid credentialId, string lastError) => SetStatusAsync(credentialId, CredentialStatus.Error, lastError);

    private async Task SetStatusAsync(Guid credentialId, CredentialStatus status, string? lastError)
    {
        using var scope = _fixture.BeginScope();
        var name = status.ToString();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = {name}, last_error = {lastError} WHERE id = {credentialId}").ConfigureAwait(false);
    }
}
