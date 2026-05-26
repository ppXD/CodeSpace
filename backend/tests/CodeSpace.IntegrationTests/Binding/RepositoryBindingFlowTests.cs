using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Binding;

[Collection(PostgresCollection.Name)]
public class RepositoryBindingFlowTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryBindingFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Bind_creates_repository_and_webhook_with_encrypted_secret()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        Guid repositoryId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repositoryId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = "acme/api"
            }).ConfigureAwait(false);
        }

        await _fixture.DrainOutboxAsync().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var repo = await db.Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId).ConfigureAwait(false);
        repo.FullPath.ShouldBe("acme/api");
        repo.Status.ShouldBe(RepositoryStatus.Active);
        repo.DeletedDate.ShouldBeNull();

        var webhook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);
        webhook.SecretEnc.ShouldNotBeNullOrEmpty();
        webhook.Active.ShouldBeTrue();
        webhook.SubscribedEvents.ShouldNotBeEmpty();
        webhook.CallbackUrl.ShouldContain($"/api/webhooks/{webhook.Id}");
    }

    [Fact]
    public async Task Bind_with_external_id_only_resolves_via_GetByExternalId()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var repositoryId = await mediator.Send(new BindRepositoryCommand
        {
            ProviderInstanceId = providerInstanceId,
            CredentialId = credentialId,
            ProjectIdentifier = "42"
        }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var repo = await db.Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId).ConfigureAwait(false);
        repo.ExternalId.ShouldBe("42");
    }

    [Fact]
    public async Task Bind_twice_for_same_repo_throws_already_bound()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);
        var identifier = $"acme/api-{Guid.NewGuid():N}";

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = identifier
            }).ConfigureAwait(false);
        }

        using var rebindScope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var rebindMediator = rebindScope.Resolve<IMediator>();
        var act = async () => await rebindMediator.Send(new BindRepositoryCommand
        {
            ProviderInstanceId = providerInstanceId,
            CredentialId = credentialId,
            ProjectIdentifier = identifier
        }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Unbind_soft_deletes_repository_and_removes_webhooks()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        Guid repositoryId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repositoryId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/unbind-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);
        }

        await _fixture.DrainOutboxAsync().ConfigureAwait(false);

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new UnbindRepositoryCommand { RepositoryId = repositoryId }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var repo = await db.Repository.AsNoTracking().SingleAsync(r => r.Id == repositoryId).ConfigureAwait(false);
        repo.DeletedDate.ShouldNotBeNull();

        var hookCount = await db.RepositoryWebhook.CountAsync(w => w.RepositoryId == repositoryId).ConfigureAwait(false);
        hookCount.ShouldBe(0);
    }

    [Fact]
    public async Task Test_returns_valid_probe_result_from_provider()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var repositoryId = await mediator.Send(new BindRepositoryCommand
        {
            ProviderInstanceId = providerInstanceId,
            CredentialId = credentialId,
            ProjectIdentifier = $"acme/test-{Guid.NewGuid():N}"
        }).ConfigureAwait(false);

        var result = await mediator.Send(new TestRepositoryBindingCommand { RepositoryId = repositoryId }).ConfigureAwait(false);

        result.IsValid.ShouldBeTrue();
        result.AuthenticatedUserName.ShouldBe("Test User");
    }

    [Fact]
    public async Task AddProviderInstanceCommand_persists_correctly()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var instanceId = await mediator.Send(new AddProviderInstanceCommand
        {
            Provider = ProviderKind.Git,
            DisplayName = "Test Git Instance",
            BaseUrl = "https://test.local",
            // OAuth credentials are required at Add time even for the Test provider — the
            // handler-level check is provider-agnostic. Values are placeholders; this
            // test cares about persistence, not the OAuth flow.
            OauthClientId = "test-client",
            OauthClientSecret = "test-secret"
        }).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var instance = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId).ConfigureAwait(false);

        instance.DisplayName.ShouldBe("Test Git Instance");
        instance.Provider.ShouldBe(ProviderKind.Git);
        instance.TeamId.ShouldBe(teamId);
    }

    [Fact]
    public async Task AddCredentialCommand_encrypts_payload_and_persists()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);
        var providerInstanceId = await SeedProviderInstanceAsync(teamId).ConfigureAwait(false);

        Guid credentialId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            credentialId = await mediator.Send(new AddCredentialCommand
            {
                ProviderInstanceId = providerInstanceId,
                DisplayName = "My PAT",
                Payload = new PatPayload { Token = "secret-token-xxx" }
            }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var encryptor = verify.Resolve<IPayloadEncryptor>();
        var serializer = verify.Resolve<ICredentialPayloadSerializer>();

        var credential = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        credential.AuthType.ShouldBe(AuthType.Pat);
        credential.EncryptedPayload.ShouldNotContain("secret-token-xxx", customMessage: "payload must not be stored in plaintext");

        var decryptedJson = encryptor.Decrypt(credential.EncryptedPayload);
        var payload = (PatPayload)serializer.Deserialize(credential.AuthType, decryptedJson);
        payload.Token.ShouldBe("secret-token-xxx");
    }

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

    private async Task<Guid> SeedProviderInstanceAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.Git,
            DisplayName = "Test",
            BaseUrl = "https://test.local"
        };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return instance.Id;
    }

    private async Task<(Guid TeamId, Guid InstanceId, Guid CredentialId)> SeedBindablePrerequisitesAsync()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);
        var instanceId = await SeedProviderInstanceAsync(teamId).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var payload = new PatPayload { Token = "pat-xxx" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload))
        };

        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (teamId, instanceId, credential.Id);
    }

    // ── Resurrect-on-rebind tests ─────────────────────────────────────────────────
    //
    // TeamCity-style identity continuity: a Repository row is durable across the
    // unbind→rebind cycle and across provider-instance recreation, so any future
    // PR/AI-review/chat data keyed off Repository.Id survives a disconnect/re-OAuth.
    // Repository.Id is the contract; these tests pin it.

    [Fact]
    public async Task Rebind_after_unbind_resurrects_same_repository_id()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);
        var identifier = $"acme/resurrect-{Guid.NewGuid():N}";

        Guid firstId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            firstId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = identifier
            }).ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new UnbindRepositoryCommand { RepositoryId = firstId }).ConfigureAwait(false);
        }

        Guid secondId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            secondId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = identifier
            }).ConfigureAwait(false);
        }

        secondId.ShouldBe(firstId, "rebind must resurrect — preserves Repository.Id so future PR / review / chat rows aren't orphaned");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var resurrected = await db.Repository.AsNoTracking().SingleAsync(r => r.Id == firstId).ConfigureAwait(false);
        resurrected.DeletedDate.ShouldBeNull();
        resurrected.Status.ShouldBe(RepositoryStatus.Active);
    }

    [Fact]
    public async Task Rebind_under_different_provider_instance_at_same_host_resurrects_and_re_points()
    {
        // The team rebuilds their OAuth app (delete + re-add same host). The new provider
        // instance has a different id but the same (provider_kind, base_url). Re-binding
        // the same repo under the new instance must resurrect the original Repository
        // row and re-point its ProviderInstanceId — NOT create an orphan new row.
        var (teamId, oldInstanceId, oldCredentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);
        var identifier = $"acme/re-point-{Guid.NewGuid():N}";

        Guid repoIdBeforeAndAfter;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            repoIdBeforeAndAfter = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = oldInstanceId,
                CredentialId = oldCredentialId,
                ProjectIdentifier = identifier
            }).ConfigureAwait(false);
        }

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new UnbindRepositoryCommand { RepositoryId = repoIdBeforeAndAfter }).ConfigureAwait(false);
        }

        // Seed a NEW provider instance with same (Provider, BaseUrl) as the old one. This
        // simulates "delete-and-recreate-the-OAuth-app" without going through the full
        // delete command (which would soft-delete the old instance — irrelevant to this
        // resurrect path, but the resurrect lookup matches on Provider+BaseUrl so it
        // finds the row even if the old instance is still active).
        var (newInstanceId, newCredentialId) = await SeedAdditionalInstanceAndCredentialAsync(teamId).ConfigureAwait(false);

        Guid rebindId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            rebindId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = newInstanceId,
                CredentialId = newCredentialId,
                ProjectIdentifier = identifier
            }).ConfigureAwait(false);
        }

        rebindId.ShouldBe(repoIdBeforeAndAfter, "resurrect must work across provider instances when (kind, base_url) matches");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var repo = await db.Repository.AsNoTracking().SingleAsync(r => r.Id == repoIdBeforeAndAfter).ConfigureAwait(false);
        repo.ProviderInstanceId.ShouldBe(newInstanceId, "row's provider_instance_id must follow the new binding");
        repo.CredentialId.ShouldBe(newCredentialId);
        repo.DeletedDate.ShouldBeNull();
    }

    /// <summary>
    /// A second provider_instance on the same host + a new credential under it. Used by
    /// the cross-instance-resurrect test to simulate "team recreated their OAuth app".
    /// </summary>
    private async Task<(Guid InstanceId, Guid CredentialId)> SeedAdditionalInstanceAndCredentialAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.Git,
            DisplayName = "Test (rebuilt)",
            BaseUrl = "https://test.local",      // same host as the seeded one — that's the point
            // Distinct OAuth client_id so the partial unique index
            // idx_provider_instance_team_provider_url_client_active doesn't collide with
            // the original (null-client) seeded instance. Models the "team rebuilt their
            // OAuth app" scenario faithfully — new app has a new client_id.
            OauthClientId = $"rebuilt-app-{Guid.NewGuid():N}"
        };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT-2",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-yyy" }))
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (instance.Id, credential.Id);
    }
}
