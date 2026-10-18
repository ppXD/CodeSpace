using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// 🟢 High fidelity: a refresh stores its pair on a connection of its own, behind the DbContext, and then points the
/// caller's credential entity at it. When that entity is TRACKED — a run command, a pull-request call, a webhook
/// registration and an issue call all load the repository's credential tracked — the DbContext's own change detection sees the
/// entity's payload differ from what it loaded, and its next <c>SaveChanges</c> writes it back. If another run rotated the
/// pair meanwhile, that write puts the consumed pair over the live one and every later run is refused.
///
/// <para>Real <see cref="CodeSpaceDbContext"/>, real refresher and writer, real Postgres; tokens last a minute, inside the
/// refresher's two-minute buffer, so every resolve rotates and two scopes interleaving is enough to tell the pairs apart.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TrackedCredentialFlushFlowTests
{
    private readonly PostgresFixture _fixture;

    public TrackedCredentialFlushFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_tracked_credential_does_not_flush_a_consumed_pair_over_a_newer_rotation()
    {
        using var gitlab = new GitLabDoorkeeperStub { ExpiresInSeconds = 60 };
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        // Scope A: a unit of work that loads the credential tracked and rotates it (rt-0 → rt-2).
        using var scopeA = _fixture.BeginScope();
        var dbA = scopeA.Resolve<CodeSpaceDbContext>();
        var repoA = await dbA.Repository.Include(r => r.ProviderInstance).Include(r => r.Credential).SingleAsync(r => r.Id == seed.RepositoryId).ConfigureAwait(false);
        var tokenA = (await scopeA.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repoA.ProviderInstance, repoA.Credential!), CancellationToken.None).ConfigureAwait(false)).Token;

        tokenA.ShouldBe("at-2");

        // Scope B: another run rotates it again (rt-2 → rt-3) while A's unit of work is still open.
        (await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId).ConfigureAwait(false)).ShouldBe("at-3");

        // A's unit of work ends.
        await dbA.SaveChangesAsync().ConfigureAwait(false);

        (await GitLabCredentialSeed.ReadStoredPayloadAsync(_fixture, seed.CredentialId).ConfigureAwait(false)).RefreshToken.ShouldBe("rt-3", "A's SaveChanges wrote the pair it rotated to over the one B stored after it");
        (await CredentialRefreshHarness.ResolveTokenAsync(_fixture, seed.RepositoryId).ConfigureAwait(false)).ShouldBe("at-4");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"a run presented a refresh token GitLab had already consumed: {string.Join(", ", gitlab.RefusedRefreshTokens())}");
    }

    [Fact]
    public async Task A_credential_disconnected_through_the_tracked_entity_after_a_refresh_stays_disconnected()
    {
        // The other direction: neutralising the refresh's write must not swallow a change the unit of work really makes.
        using var gitlab = new GitLabDoorkeeperStub { ExpiresInSeconds = 60 };
        gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");
        var seed = await GitLabCredentialSeed.SeedAsync(_fixture, gitlab.BaseUrl, "at-0", "rt-0").ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var repo = await db.Repository.Include(r => r.ProviderInstance).Include(r => r.Credential).SingleAsync(r => r.Id == seed.RepositoryId).ConfigureAwait(false);

        await scope.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential!), CancellationToken.None).ConfigureAwait(false);

        repo.Credential!.Status = CredentialStatus.Revoked;
        repo.Credential.EncryptedPayload = string.Empty;
        await db.SaveChangesAsync().ConfigureAwait(false);

        var row = await GitLabCredentialSeed.ReadCredentialAsync(_fixture, seed.CredentialId).ConfigureAwait(false);

        row.Status.ShouldBe(CredentialStatus.Revoked);
        row.EncryptedPayload.ShouldBeEmpty("a disconnect made through the tracked entity must still blank the token");
    }
}
