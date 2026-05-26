using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using CodeSpace.Messages.Queries.ProviderInstances;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Binding;

[Collection(PostgresCollection.Name)]
public class RepositoryQueryFlowTests
{
    private readonly PostgresFixture _fixture;

    public RepositoryQueryFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task ListProviderInstances_returns_all_for_team()
    {
        var teamId = await SeedTeamAsync().ConfigureAwait(false);
        await SeedProviderInstanceAsync(teamId, "Instance A").ConfigureAwait(false);
        await SeedProviderInstanceAsync(teamId, "Instance B").ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new ListProviderInstancesQuery()).ConfigureAwait(false);

        result.Count.ShouldBe(2);
        result.ShouldContain(p => p.DisplayName == "Instance A");
        result.ShouldContain(p => p.DisplayName == "Instance B");
    }

    [Fact]
    public async Task ListCredentials_does_not_expose_encrypted_payload()
    {
        var (teamId, providerInstanceId, _) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new ListCredentialsQuery { ProviderInstanceId = providerInstanceId }).ConfigureAwait(false);

        result.Count.ShouldBe(1);
        var cred = result.Single();
        cred.AuthType.ShouldBe(AuthType.Pat);
        cred.Status.ShouldBe(CredentialStatus.Active);

        // Confirm: CredentialSummary has no field exposing the encrypted payload
        typeof(Messages.Dtos.Credentials.CredentialSummary).GetProperties().ShouldNotContain(p => p.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ListRepositories_returns_only_non_deleted()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        Guid keptRepoId;
        Guid deletedRepoId;
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            keptRepoId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/kept-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);

            deletedRepoId = await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = $"acme/deleted-{Guid.NewGuid():N}"
            }).ConfigureAwait(false);

            await mediator.Send(new UnbindRepositoryCommand { RepositoryId = deletedRepoId }).ConfigureAwait(false);
        }

        using var listScope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var listMediator = listScope.Resolve<IMediator>();
        var result = await listMediator.Send(new ListRepositoriesQuery()).ConfigureAwait(false);

        result.Select(r => r.Id).ShouldContain(keptRepoId);
        result.Select(r => r.Id).ShouldNotContain(deletedRepoId);
    }

    [Fact]
    public async Task GetRepository_returns_detail_with_active_webhook_count()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var repoId = await mediator.Send(new BindRepositoryCommand
        {
            ProviderInstanceId = providerInstanceId,
            CredentialId = credentialId,
            ProjectIdentifier = $"acme/get-{Guid.NewGuid():N}"
        }).ConfigureAwait(false);

        await _fixture.DrainOutboxAsync().ConfigureAwait(false);

        var detail = await mediator.Send(new GetRepositoryQuery { RepositoryId = repoId }).ConfigureAwait(false);

        detail.ShouldNotBeNull();
        detail!.Id.ShouldBe(repoId);
        detail.ActiveWebhooksCount.ShouldBe(1);
        detail.Status.ShouldBe(RepositoryStatus.Active);
    }

    [Fact]
    public async Task GetRepository_returns_null_for_unknown_id()
    {
        // Admin bypasses tenancy + entity dereference; missing row surfaces as null from handler.
        var teamId = await SeedTeamAsync().ConfigureAwait(false);
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var detail = await mediator.Send(new GetRepositoryQuery { RepositoryId = Guid.NewGuid() }).ConfigureAwait(false);

        detail.ShouldBeNull();
    }

    [Fact]
    public async Task ListAccessibleRepositories_returns_provider_canned_repos()
    {
        var (teamId, _, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new ListAccessibleRepositoriesQuery { CredentialId = credentialId }).ConfigureAwait(false);

        result.Items.Count.ShouldBe(3);
        result.Items.ShouldContain(r => r.FullPath == "acme/api");
        result.Items.ShouldContain(r => r.FullPath == "acme/web");
        result.Items.ShouldContain(r => r.FullPath == "acme/cli");
    }

    [Fact]
    public async Task BindRepositoriesBulk_binds_all_in_one_transaction()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new BindRepositoriesBulkCommand
        {
            ProviderInstanceId = providerInstanceId,
            CredentialId = credentialId,
            ProjectIdentifiers = new[] { $"acme/a-{suffix}", $"acme/b-{suffix}", $"acme/c-{suffix}" }
        }).ConfigureAwait(false);

        result.SuccessCount.ShouldBe(3);
        result.FailureCount.ShouldBe(0);
        result.Items.Count.ShouldBe(3);
        result.Items.ShouldAllBe(i => i.RepositoryId != null);
    }

    [Fact]
    public async Task BindRepositoriesBulk_rolls_back_all_on_any_failure()
    {
        var (teamId, providerInstanceId, credentialId) = await SeedBindablePrerequisitesAsync().ConfigureAwait(false);
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
        var duplicate = $"acme/dup-{suffix}";

        // Pre-bind one
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new BindRepositoryCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifier = duplicate
            }).ConfigureAwait(false);
        }

        // Bulk attempt where the 2nd item is the already-bound one
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            var act = async () => await mediator.Send(new BindRepositoriesBulkCommand
            {
                ProviderInstanceId = providerInstanceId,
                CredentialId = credentialId,
                ProjectIdentifiers = new[] { $"acme/x-{suffix}", duplicate, $"acme/y-{suffix}" }
            }).ConfigureAwait(false);

            await act.ShouldThrowAsync<InvalidOperationException>().ConfigureAwait(false);
        }

        // Verify: ONLY the original `duplicate` exists; `x-` and `y-` got rolled back
        using var verifyScope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);
        var listMediator = verifyScope.Resolve<IMediator>();
        var allRepos = await listMediator.Send(new ListRepositoriesQuery()).ConfigureAwait(false);
        allRepos.Where(r => r.FullPath.Contains(suffix)).Select(r => r.FullPath).Single().ShouldBe(duplicate);
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

    private async Task<Guid> SeedProviderInstanceAsync(Guid teamId, string displayName = "Test")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.Git,
            DisplayName = displayName,
            BaseUrl = $"https://test-{Guid.NewGuid():N}.local"
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
}
