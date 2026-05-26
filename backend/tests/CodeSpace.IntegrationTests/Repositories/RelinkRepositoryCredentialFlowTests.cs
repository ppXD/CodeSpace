using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Repositories;

/// <summary>
/// Pins the credential re-link recovery path. After a credential disconnect cascades a
/// repo to Status=Error, the operator must be able to point it at another active
/// credential of the same provider instance and have it flip back to Active in one call.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RelinkRepositoryCredentialFlowTests
{
    private readonly PostgresFixture _fixture;

    public RelinkRepositoryCredentialFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Relink_to_another_active_credential_flips_repo_back_to_Active()
    {
        var ctx = await SeedTwoCredentialsOneRepoAsync().ConfigureAwait(false);

        // Simulate the "credential disconnected" state: repo points at oldCred (revoked),
        // status=Error with a remediation message. This is exactly what the cascade in
        // RevokeCredentialCommandHandler produces.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var repo = await db.Repository.SingleAsync(r => r.Id == ctx.RepoId).ConfigureAwait(false);
            repo.Status = RepositoryStatus.Error;
            repo.LastError = "Credential disconnected. Re-link this repository...";
            var oldCred = await db.Credential.SingleAsync(c => c.Id == ctx.OldCredId).ConfigureAwait(false);
            oldCred.Status = CredentialStatus.Revoked;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), ctx.TeamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new RelinkRepositoryCredentialCommand { RepositoryId = ctx.RepoId, NewCredentialId = ctx.NewCredId }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var dbv = verify.Resolve<CodeSpaceDbContext>();
        var repoAfter = await dbv.Repository.AsNoTracking().SingleAsync(r => r.Id == ctx.RepoId).ConfigureAwait(false);

        repoAfter.CredentialId.ShouldBe(ctx.NewCredId);
        repoAfter.Status.ShouldBe(RepositoryStatus.Active);
        repoAfter.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task Relink_to_credential_of_different_provider_instance_is_rejected()
    {
        var ctx = await SeedTwoCredentialsOneRepoAsync().ConfigureAwait(false);

        // Create a SECOND provider instance + a credential on it. Relinking the repo to
        // that credential must fail — would mean the OAuth token is for the wrong host.
        Guid foreignCredId;
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var encryptor = scope.Resolve<IPayloadEncryptor>();
            var serializer = scope.Resolve<ICredentialPayloadSerializer>();

            var foreignInstance = new ProviderInstance
            {
                Id = Guid.NewGuid(),
                TeamId = ctx.TeamId,
                Provider = ProviderKind.GitLab,
                DisplayName = "GitLab",
                BaseUrl = "https://gitlab.com",
                OauthClientId = "gl-client",
                OauthClientSecretEnc = encryptor.Encrypt("secret")
            };
            var foreignCred = new Credential
            {
                Id = Guid.NewGuid(),
                TeamId = ctx.TeamId,
                ProviderInstanceId = foreignInstance.Id,
                AuthType = AuthType.OAuth,
                DisplayName = "foreign",
                EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new OAuthPayload { AccessToken = "at" })),
                Status = CredentialStatus.Active
            };
            db.ProviderInstance.Add(foreignInstance);
            db.Credential.Add(foreignCred);
            await db.SaveChangesAsync().ConfigureAwait(false);
            foreignCredId = foreignCred.Id;
        }

        using var caller = _fixture.BeginScopeAs(Guid.NewGuid(), ctx.TeamId, Roles.Admin);
        var mediator = caller.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new RelinkRepositoryCredentialCommand { RepositoryId = ctx.RepoId, NewCredentialId = foreignCredId })).ConfigureAwait(false);

        ex.Message.ShouldContain("different provider instance");
    }

    [Fact]
    public async Task Relink_to_revoked_credential_is_rejected()
    {
        var ctx = await SeedTwoCredentialsOneRepoAsync().ConfigureAwait(false);

        // Revoke the candidate credential first — should be ineligible for relink.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var c = await db.Credential.SingleAsync(c => c.Id == ctx.NewCredId).ConfigureAwait(false);
            c.Status = CredentialStatus.Revoked;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var caller = _fixture.BeginScopeAs(Guid.NewGuid(), ctx.TeamId, Roles.Admin);
        var mediator = caller.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new RelinkRepositoryCredentialCommand { RepositoryId = ctx.RepoId, NewCredentialId = ctx.NewCredId })).ConfigureAwait(false);

        ex.Message.ShouldContain("Revoked");
        ex.Message.ShouldContain("Active");
    }

    // ── Seed helper ─────────────────────────────────────────────────────────────

    private record SeedContext(Guid TeamId, Guid InstanceId, Guid OldCredId, Guid NewCredId, Guid RepoId);

    private async Task<SeedContext> SeedTwoCredentialsOneRepoAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"o-{suffix}@x", Name = "owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = owner.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.GitHub,
            DisplayName = "GitHub",
            BaseUrl = "https://github.com",
            OauthClientId = "gh-client",
            OauthClientSecretEnc = encryptor.Encrypt("secret")
        };

        var oldCred = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.OAuth,
            DisplayName = "alice's GitHub",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new OAuthPayload { AccessToken = "at-1" })),
            Status = CredentialStatus.Active
        };
        var newCred = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.OAuth,
            DisplayName = "bob's GitHub",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new OAuthPayload { AccessToken = "at-2" })),
            Status = CredentialStatus.Active
        };

        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            CredentialId = oldCred.Id,
            ExternalId = "1",
            NamespacePath = "acme",
            Name = "api",
            FullPath = "acme/api",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://github.com/acme/api",
            Status = RepositoryStatus.Active
        };

        db.User.Add(owner);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(oldCred);
        db.Credential.Add(newCred);
        db.Repository.Add(repo);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeedContext(team.Id, instance.Id, oldCred.Id, newCred.Id, repo.Id);
    }
}
