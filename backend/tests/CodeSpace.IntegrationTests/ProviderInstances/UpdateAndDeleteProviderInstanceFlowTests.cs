using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.ProviderInstances;

/// <summary>
/// Update + Delete coverage for provider_instance. Together these unstick the case where
/// an early-created provider has no OAuth credentials (so it's filtered out of the
/// "connectable" list) and the user has no way back — they now have both an Edit and a
/// Remove escape hatch.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UpdateAndDeleteProviderInstanceFlowTests
{
    private readonly PostgresFixture _fixture;

    public UpdateAndDeleteProviderInstanceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ── Update ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_fills_in_missing_OAuth_credentials_on_existing_provider()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: false).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new UpdateProviderInstanceCommand
        {
            ProviderInstanceId = instanceId,
            OauthClientId = "new-client-id",
            OauthClientSecret = "new-secret",
            OauthDefaultScopes = new[] { "repo", "read:user" }
        }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var encryptor = verify.Resolve<IPayloadEncryptor>();
        var saved = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);

        saved.OauthClientId.ShouldBe("new-client-id");
        encryptor.Decrypt(saved.OauthClientSecretEnc!).ShouldBe("new-secret");
        saved.OauthDefaultScopes.ShouldBe(new[] { "repo", "read:user" });
    }

    [Fact]
    public async Task Update_with_empty_OAuth_secret_preserves_existing_secret()
    {
        // The form's "leave blank to keep" semantics — an empty password input must NEVER
        // accidentally wipe a stored secret. Asserts the handler's
        // !string.IsNullOrEmpty(OauthClientSecret) guard.
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new UpdateProviderInstanceCommand
        {
            ProviderInstanceId = instanceId,
            DisplayName = "Renamed",
            OauthClientSecret = ""   // empty string = "no rotate"
        }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var encryptor = verify.Resolve<IPayloadEncryptor>();
        var saved = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);

        saved.DisplayName.ShouldBe("Renamed");
        encryptor.Decrypt(saved.OauthClientSecretEnc!).ShouldBe("original-secret");
    }

    [Fact]
    public async Task Update_rejects_when_post_edit_tuple_collides_with_another_active_instance()
    {
        // Both rows in the same team:
        //   firstId  → github.com   + client-id="client-id"  (OAuth-ready, seeded with that literal client)
        //   secondId → github.acme  + null client
        // Trying to point secondId at github.com WITHOUT setting its own client_id collides
        // (both rows would have client_id=null on github.com, which is the duplicate case).
        var (teamId, firstId) = await SeedProviderAsync(withOAuth: true, baseUrl: "https://github.com").ConfigureAwait(false);
        var (_, secondId) = await SeedProviderAsync(withOAuth: false, baseUrl: "https://github.acme.local", existingTeamId: teamId).ConfigureAwait(false);

        // Make firstId's row null-client so the post-edit secondId collides with it.
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var first = await db.ProviderInstance.SingleAsync(p => p.Id == firstId).ConfigureAwait(false);
            first.OauthClientId = null;
            first.OauthClientSecretEnc = null;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var caller = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = caller.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new UpdateProviderInstanceCommand
            {
                ProviderInstanceId = secondId,
                BaseUrl = "https://github.com"   // collides with firstId (both null client_id)
            })).ConfigureAwait(false);

        ex.Message.ShouldContain("github.com");
    }

    [Fact]
    public async Task Update_allows_moving_to_same_host_when_client_id_differs()
    {
        // Mirror of the Add multi-app test, but via Edit: take a non-OAuth provider on a
        // dummy host, fill it in with a NEW client_id that doesn't collide with the
        // existing github.com row → must succeed (the multi-app pattern works through
        // Edit too, not only through fresh Add).
        var (teamId, firstId) = await SeedProviderAsync(withOAuth: true, baseUrl: "https://github.com").ConfigureAwait(false);
        var (_, secondId) = await SeedProviderAsync(withOAuth: false, baseUrl: "https://github.acme.local", existingTeamId: teamId).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new UpdateProviderInstanceCommand
        {
            ProviderInstanceId = secondId,
            BaseUrl = "https://github.com",
            OauthClientId = "different-client",
            OauthClientSecret = "different-secret"
        }).ConfigureAwait(false);

        // Both rows now exist on github.com with different client_ids — exactly the
        // intended multi-app shape. Sanity-check by re-loading.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rows = await db.ProviderInstance.AsNoTracking()
            .Where(p => (p.Id == firstId || p.Id == secondId) && p.DeletedDate == null)
            .OrderBy(p => p.OauthClientId)
            .ToListAsync().ConfigureAwait(false);

        rows.Count.ShouldBe(2);
        rows.All(r => r.BaseUrl == "https://github.com").ShouldBeTrue();
        rows.Select(r => r.OauthClientId).ShouldBe(new[] { "client-id", "different-client" }, ignoreOrder: true);
    }

    [Fact]
    public async Task Update_rejects_unknown_instance_with_not_found()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await Should.ThrowAsync<KeyNotFoundException>(
            () => mediator.Send(new UpdateProviderInstanceCommand
            {
                ProviderInstanceId = Guid.NewGuid(),
                DisplayName = "Whatever"
            })).ConfigureAwait(false);
    }

    // ── Delete ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_soft_deletes_provider_when_no_dependencies()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: false).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var saved = await db.ProviderInstance.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);

        saved.DeletedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task Delete_cascade_revokes_active_credentials()
    {
        // The cleanup motivation: a deleted provider's tokens are useless. If we left them
        // Active in the DB, ListCredentials would still show them and the UI would lie.
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var cred = await db.Credential.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);

        cred.Status.ShouldBe(CredentialStatus.Revoked);
        cred.DeletedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task Delete_without_force_refuses_when_active_repositories_still_bound()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId).ConfigureAwait(false);
        await SeedRepositoryAsync(teamId, instanceId, credentialId).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId, Force = false })).ConfigureAwait(false);

        // Error names the repo count and points the operator at the cascade option,
        // not just "go find them yourself".
        ex.Message.ShouldContain("repositor");
        ex.Message.ShouldContain("Unbind");
        ex.Message.ShouldContain("unbind all");
    }

    [Fact]
    public async Task Delete_with_force_cascades_unbind_then_removes_provider()
    {
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: true).ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(teamId, instanceId).ConfigureAwait(false);
        var repoIdA = await SeedRepositoryAsync(teamId, instanceId, credentialId, name: "api").ConfigureAwait(false);
        var repoIdB = await SeedRepositoryAsync(teamId, instanceId, credentialId, name: "web").ConfigureAwait(false);

        DeleteProviderInstanceResult result;

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            result = await mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId, Force = true }).ConfigureAwait(false);
        }

        result.UnboundRepositoryCount.ShouldBe(2);
        result.RevokedCredentialCount.ShouldBe(1);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // Provider: soft-deleted.
        var providerRow = await db.ProviderInstance.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);
        providerRow.DeletedDate.ShouldNotBeNull();

        // Both repos: soft-deleted.
        var repoRows = await db.Repository.AsNoTracking().IgnoreQueryFilters().Where(r => r.Id == repoIdA || r.Id == repoIdB).ToListAsync().ConfigureAwait(false);
        repoRows.Count.ShouldBe(2);
        repoRows.ShouldAllBe(r => r.DeletedDate != null);

        // Credential: revoked.
        var credRow = await db.Credential.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        credRow.Status.ShouldBe(CredentialStatus.Revoked);
        credRow.DeletedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task Delete_with_force_when_no_repositories_still_works()
    {
        // Force=true is a superset — must not require any active repos to exist. Tests
        // the no-cascade path through the same flag so callers don't have to branch.
        var (teamId, instanceId) = await SeedProviderAsync(withOAuth: false).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId, Force = true }).ConfigureAwait(false);

        result.UnboundRepositoryCount.ShouldBe(0);
        result.RevokedCredentialCount.ShouldBe(0);
    }

    // ── seed helpers ────────────────────────────────────────────────────────────

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team", OwnerUserId = owner.Id };

        db.User.Add(owner);
        db.Team.Add(team);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return team.Id;
    }

    private async Task<(Guid TeamId, Guid InstanceId)> SeedProviderAsync(bool withOAuth, string? baseUrl = null, Guid? existingTeamId = null)
    {
        var teamId = existingTeamId ?? await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.GitHub,
            DisplayName = "GitHub",
            BaseUrl = baseUrl ?? "https://github.com",
            OauthClientId = withOAuth ? "client-id" : null,
            OauthClientSecretEnc = withOAuth ? encryptor.Encrypt("original-secret") : null
        };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (teamId, instance.Id);
    }

    private async Task<Guid> SeedCredentialAsync(Guid teamId, Guid instanceId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var payload = new OAuthPayload { AccessToken = "at", RefreshToken = "rt" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instanceId,
            AuthType = AuthType.OAuth,
            DisplayName = "OAuth",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)),
            Status = CredentialStatus.Active
        };

        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, Guid instanceId, Guid credentialId, string name = "api")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instanceId,
            CredentialId = credentialId,
            ExternalId = Guid.NewGuid().ToString("N"),
            NamespacePath = "acme",
            Name = name,
            FullPath = $"acme/{name}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = $"https://github.com/acme/{name}",
            Status = RepositoryStatus.Active
        };

        db.Repository.Add(repo);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return repo.Id;
    }
}
