using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Binding;

/// <summary>
/// Bind-time pre-flight scope checks. RepositoryBindingService asks IScopeChecker whether
/// the credential's granted scopes cover both <c>IRepositoryCatalogCapability</c> and
/// <c>IWebhookRegistrationCapability</c> BEFORE any wire call. These tests exercise the
/// fail-fast paths so an OAuth user with insufficient scopes gets a structured error
/// instead of dying mid-bind with a cryptic 403.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ScopePreflightFlowTests
{
    private readonly PostgresFixture _fixture;

    public ScopePreflightFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Bind_rejects_OAuth_credential_missing_webhook_scope_with_typed_exception()
    {
        // GitLab webhook registration needs the `api` scope. A credential granted only
        // `read_repository` finishes OAuth but can't register webhooks — pre-flight must
        // refuse the bind before we hit the wire.
        var (teamId, instanceId, credentialId) = await SeedOAuthBindablePrerequisitesAsync(
            ProviderKind.GitLab, grantedScopes: new[] { "read_repository" }).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<ProviderInsufficientScopeException>(
            () => mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = instanceId,
                CredentialId = credentialId,
                ProjectIdentifier = "acme/api"
            })).ConfigureAwait(false);

        ex.ProviderKind.ShouldBe(ProviderKind.GitLab);
        ex.MissingScopes.ShouldContain("api");
    }

    [Fact]
    public async Task Bind_rejects_GitHub_OAuth_credential_lacking_repo_scope()
    {
        var (teamId, instanceId, credentialId) = await SeedOAuthBindablePrerequisitesAsync(
            ProviderKind.GitHub, grantedScopes: new[] { "read:user" }).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var ex = await Should.ThrowAsync<ProviderInsufficientScopeException>(
            () => mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = instanceId,
                CredentialId = credentialId,
                ProjectIdentifier = "acme/api"
            })).ConfigureAwait(false);

        ex.ProviderKind.ShouldBe(ProviderKind.GitHub);
        // Catalog wants [repo OR public_repo]; webhook wants [repo OR admin:repo_hook].
        // Neither alternative is satisfied — exception should name a missing scope.
        ex.MissingScopes.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task PAT_credential_skips_scope_pre_flight()
    {
        // PAT tokens have no Scopes column on our side — pre-flight must NOT block them.
        // (Existing PAT-based bind tests already exercise the happy path; this asserts the
        //  early-return path explicitly so a regression to "scope-check everything" surfaces.)
        var (teamId, instanceId, credentialId) = await SeedPatBindablePrerequisitesAsync(ProviderKind.Git).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        // Must NOT throw insufficient_scope. The bind goes through and lands a repo. We
        // assert via the absence of the typed exception — any other failure mode would
        // surface as a different exception type.
        await mediator.Send(new BindRepositoryCommand
        {
            ProviderInstanceId = instanceId,
            CredentialId = credentialId,
            ProjectIdentifier = "acme/foo"
        }).ConfigureAwait(false);
    }

    // ── seed helpers ───────────────────────────────────────────────────────────

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

    private async Task<(Guid TeamId, Guid InstanceId, Guid CredentialId)> SeedOAuthBindablePrerequisitesAsync(ProviderKind kind, string[] grantedScopes)
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = kind,
            DisplayName = $"{kind} test",
            BaseUrl = kind == ProviderKind.GitHub ? "https://github.com" : "https://gitlab.com"
        };

        var payload = new OAuthPayload { AccessToken = "at", RefreshToken = "rt" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.OAuth,
            DisplayName = "OAuth",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)),
            Scopes = grantedScopes.ToList()
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (teamId, instance.Id, credential.Id);
    }

    private async Task<(Guid TeamId, Guid InstanceId, Guid CredentialId)> SeedPatBindablePrerequisitesAsync(ProviderKind kind)
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = kind,
            DisplayName = "Test",
            BaseUrl = "https://test.local"
        };

        var payload = new PatPayload { Token = "pat-xxx" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload))
            // No Scopes — PAT credentials don't carry them on our side
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (teamId, instance.Id, credential.Id);
    }
}
