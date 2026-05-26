using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// Pins the revoke contract: local credential is always marked Revoked + payload cleared,
/// even when the provider's revocation endpoint fails. The provider call is best-effort.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RevokeCredentialFlowTests
{
    private readonly PostgresFixture _fixture;

    public RevokeCredentialFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Revoke_OAuth_calls_provider_and_clears_local_payload()
    {
        var (userId, teamId, credId) = await SeedOAuthCredentialAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("ignored"));

        RevokeCredentialResult result;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            // Admin bypasses tenancy; override OAuth registry with our recording stub.
            using var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());
            result = await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);
        }

        result.CredentialId.ShouldBe(credId);
        result.ProviderAcknowledged.ShouldBeTrue();
        stub.LastRevoke.ShouldNotBeNull();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var cred = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credId).ConfigureAwait(false);
        cred.Status.ShouldBe(CredentialStatus.Revoked);
        cred.EncryptedPayload.ShouldBe(string.Empty);
        cred.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Revoke_clears_local_payload_even_when_provider_throws()
    {
        var (userId, teamId, credId) = await SeedOAuthCredentialAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("ignored")) { RevokeShouldThrow = true };

        RevokeCredentialResult result;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            using var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());
            result = await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);
        }

        result.ProviderAcknowledged.ShouldBeFalse();
        result.ProviderError.ShouldNotBeNullOrEmpty();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var cred = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credId).ConfigureAwait(false);
        // Local revocation is unconditional — the credential is unusable regardless of provider outcome.
        cred.Status.ShouldBe(CredentialStatus.Revoked);
        cred.EncryptedPayload.ShouldBe(string.Empty);
        cred.LastError.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Revoke_PAT_skips_provider_call_and_marks_revoked()
    {
        var (userId, teamId, credId) = await SeedPatCredentialAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("ignored"));

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        using var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());
        var result = await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);

        result.ProviderAcknowledged.ShouldBeTrue();
        // The stub must never have been called — PAT has no provider-side revoke endpoint.
        stub.LastRevoke.ShouldBeNull();
    }

    [Fact]
    public async Task Revoke_already_revoked_is_idempotent()
    {
        var (userId, teamId, credId) = await SeedOAuthCredentialAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("ignored"));

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        using (var first = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            await first.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);
        }

        // Second call must not throw and must not hit the provider again.
        stub.LastRevoke = null;

        RevokeCredentialResult second;

        using (var again = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            second = await again.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);
        }

        second.ProviderAcknowledged.ShouldBeTrue();
        stub.LastRevoke.ShouldBeNull("second revoke should not re-call the provider");
    }

    [Fact]
    public async Task Revoke_cascades_marks_dependent_repositories_as_Error()
    {
        // Setup: credential with 2 active repos bound through it, plus 1 unrelated repo
        // bound through a different credential (must stay Active).
        var (userId, teamId, credId) = await SeedOAuthCredentialAsync().ConfigureAwait(false);
        var (_, _, otherCredId) = await SeedOAuthCredentialAsync().ConfigureAwait(false);

        var repoIdsForCred = await SeedRepositoriesAsync(teamId, credId, count: 2).ConfigureAwait(false);
        var unrelatedRepoId = (await SeedRepositoriesAsync(teamId, otherCredId, count: 1).ConfigureAwait(false)).Single();

        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("ignored"));

        RevokeCredentialResult result;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        using (var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            result = await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);
        }

        result.AffectedRepositoryCount.ShouldBe(2);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // Both repos for the revoked credential flipped to Error with a clear message
        // that names the remediation path (re-link or unbind).
        var affected = await db.Repository.AsNoTracking().Where(r => repoIdsForCred.Contains(r.Id)).ToListAsync().ConfigureAwait(false);
        affected.Count.ShouldBe(2);
        affected.ShouldAllBe(r => r.Status == RepositoryStatus.Error);
        affected.ShouldAllBe(r => r.LastError != null && r.LastError.Contains("disconnected"));
        affected.ShouldAllBe(r => r.LastError != null && r.LastError.Contains("Re-link"));
        affected.ShouldAllBe(r => r.DeletedDate == null, "repos must NOT be soft-deleted — webhooks keep ingesting");
        // CredentialId is preserved as audit trail; the UI reads Credential.Status to know.
        affected.ShouldAllBe(r => r.CredentialId == credId);

        // Unrelated repo (different credential) is untouched.
        var unrelated = await db.Repository.AsNoTracking().SingleAsync(r => r.Id == unrelatedRepoId).ConfigureAwait(false);
        unrelated.Status.ShouldBe(RepositoryStatus.Active);
    }

    [Fact]
    public async Task Revoke_with_no_dependent_repositories_returns_zero_count()
    {
        var (userId, teamId, credId) = await SeedOAuthCredentialAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("ignored"));

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        using var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());

        var result = await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);

        result.AffectedRepositoryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Revoke_called_twice_still_heals_orphan_Active_repositories_on_second_call()
    {
        // Regression guard: an earlier version of the handler short-circuited when the
        // credential was already Revoked, which meant the cascade was skipped — leaving
        // any Active-status repo pointing at a Revoked credential stuck out of sync.
        // The fix makes the cascade idempotent and unconditional, so a second call
        // (or any future call) self-heals. This test pins that behaviour by:
        //   1. Revoking once with a repo already in place                  → repo → Error
        //   2. Resetting that repo back to Active (simulates the orphan)
        //   3. Revoking again on the same already-Revoked credential       → repo → Error
        var (userId, teamId, credId) = await SeedOAuthCredentialAsync().ConfigureAwait(false);
        var repoIds = await SeedRepositoriesAsync(teamId, credId, count: 1).ConfigureAwait(false);
        var repoId = repoIds.Single();

        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("ignored"));

        // First revoke — should mark the repo Error.
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        using (var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);
        }

        // Simulate the orphan-Active state: flip the repo back to Active even though the
        // credential is now Revoked. This is what we see in prod when the cascade didn't
        // exist at the time of the original revoke.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var repo = await db.Repository.SingleAsync(r => r.Id == repoId).ConfigureAwait(false);
            repo.Status = RepositoryStatus.Active;
            repo.LastError = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        // Second revoke — must still cascade and clean up the orphan, NOT short-circuit.
        RevokeCredentialResult second;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        using (var revoke = scope.BeginLifetimeScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            second = await revoke.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credId }).ConfigureAwait(false);
        }

        second.AffectedRepositoryCount.ShouldBe(1, "second revoke should still heal the orphaned Active repo");

        using var verify = _fixture.BeginScope();
        var dbv = verify.Resolve<CodeSpaceDbContext>();
        var repoAfter = await dbv.Repository.AsNoTracking().SingleAsync(r => r.Id == repoId).ConfigureAwait(false);
        repoAfter.Status.ShouldBe(RepositoryStatus.Error);
        repoAfter.LastError.ShouldNotBeNullOrEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>> SeedRepositoriesAsync(Guid teamId, Guid credId, int count)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var providerInstanceId = await db.Credential.AsNoTracking().Where(c => c.Id == credId).Select(c => c.ProviderInstanceId).SingleAsync().ConfigureAwait(false);
        var ids = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var repo = new Repository
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                ProviderInstanceId = providerInstanceId,
                CredentialId = credId,
                ExternalId = Guid.NewGuid().ToString("N"),
                NamespacePath = "acme",
                Name = $"repo-{i}",
                FullPath = $"acme/repo-{i}",
                DefaultBranch = "main",
                Visibility = RepositoryVisibility.Private,
                WebUrl = $"https://example.test/acme/repo-{i}",
                Status = RepositoryStatus.Active
            };
            db.Repository.Add(repo);
            ids.Add(repo.Id);
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        return ids;
    }


    private async Task<(Guid UserId, Guid TeamId, Guid CredId)> SeedOAuthCredentialAsync()
    {
        return await SeedCredentialAsync(AuthType.OAuth, new OAuthPayload { AccessToken = "at", RefreshToken = "rt" }).ConfigureAwait(false);
    }

    private async Task<(Guid UserId, Guid TeamId, Guid CredId)> SeedPatCredentialAsync()
    {
        return await SeedCredentialAsync(AuthType.Pat, new PatPayload { Token = "glpat_xxx" }).ConfigureAwait(false);
    }

    private async Task<(Guid UserId, Guid TeamId, Guid CredId)> SeedCredentialAsync(AuthType authType, CredentialPayload payload)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.GitLab,
            DisplayName = "instance",
            BaseUrl = $"https://gitlab-{suffix}.local",
            OauthClientId = "client",
            OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var cred = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = authType,
            DisplayName = "test-cred",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)),
            Status = CredentialStatus.Active
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(cred);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, team.Id, cred.Id);
    }

    private static OAuthTokenResponse BuildToken(string accessToken) => new() { AccessToken = accessToken };

    private sealed class SingleProviderRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;
        public SingleProviderRegistry(IOAuthClient client) { _client = client; }
        public IOAuthClient Get(ProviderKind kind) => _client;
    }
}
