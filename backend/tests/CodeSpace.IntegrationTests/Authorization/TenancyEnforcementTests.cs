using Autofac;
using CodeSpace.Core.Authorization;
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
using CodeSpace.Messages.Queries.Credentials;
using CodeSpace.Messages.Queries.ProviderInstances;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// Pins the cross-tenant access invariant for every team-scoped command/query: a non-admin
/// user whose X-Team-Id header targets a team they do not belong to gets
/// TenantAccessDeniedException before any handler runs.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenancyEnforcementTests
{
    private readonly PostgresFixture _fixture;

    public TenancyEnforcementTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task ListRepositoriesQuery_cross_team_throws_TenantAccessDenied()
    {
        var (userA, _, teamB) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamB);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new ListRepositoriesQuery()).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task ListRepositoriesQuery_own_team_succeeds()
    {
        var (userA, teamA, _) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamA);
        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new ListRepositoriesQuery()).ConfigureAwait(false);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListCredentialsQuery_cross_team_throws()
    {
        var (userA, _, teamB) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamB);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new ListCredentialsQuery()).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task ListProviderInstancesQuery_cross_team_throws()
    {
        var (userA, _, teamB) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamB);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new ListProviderInstancesQuery()).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task AddProviderInstanceCommand_cross_team_throws()
    {
        var (userA, _, teamB) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamB);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new AddProviderInstanceCommand
        {
            Provider = ProviderKind.Git,
            DisplayName = "evil",
            BaseUrl = "https://evil.local"
        }).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task AddCredentialCommand_cross_team_throws()
    {
        var (userA, _, teamB, instanceB) = await SeedTwoTeamsWithInstanceAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamB);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new AddCredentialCommand
        {
            ProviderInstanceId = instanceB,
            DisplayName = "evil-pat",
            Payload = new PatPayload { Token = "stolen" }
        }).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task BindRepositoryCommand_cross_team_throws()
    {
        var (userA, _, teamB, instanceB, credentialB) = await SeedTwoTeamsWithCredentialAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamB);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new BindRepositoryCommand
        {
            ProviderInstanceId = instanceB,
            CredentialId = credentialB,
            ProjectIdentifier = "stolen/repo"
        }).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task GetRepositoryQuery_cross_team_throws()
    {
        var (userA, teamA, repoB) = await SeedTwoTeamsWithBoundRepoAsync().ConfigureAwait(false);

        // userA in teamA tries to read teamB's repo by id — header is teamA, entity is teamB → cross-check fails.
        using var scope = _fixture.BeginScopeAs(userA, teamA);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new GetRepositoryQuery { RepositoryId = repoB }).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task UnbindRepositoryCommand_cross_team_throws()
    {
        var (userA, teamA, repoB) = await SeedTwoTeamsWithBoundRepoAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamA);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new UnbindRepositoryCommand { RepositoryId = repoB }).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task ListAccessibleRepositoriesQuery_cross_team_throws()
    {
        var (userA, teamA, _, _, credentialB) = await SeedTwoTeamsWithCredentialAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamA);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new ListAccessibleRepositoriesQuery { CredentialId = credentialB }).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Admin_role_bypasses_tenancy_check()
    {
        var (_, _, teamB) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamB, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new ListRepositoriesQuery()).ConfigureAwait(false);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Anonymous_user_throws_TenantAccessDenied()
    {
        var (_, _, teamB) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(null, teamB);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new ListRepositoriesQuery()).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Missing_X_Team_Id_header_throws_TenantAccessDenied()
    {
        var (userA, _, _) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userA, teamId: null);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new ListRepositoriesQuery()).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        ex.Message.ShouldContain("X-Team-Id");
    }

    // ── Seed helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid UserA, Guid TeamA, Guid TeamB)> SeedTwoTeamsAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var userA = new User { Id = Guid.NewGuid(), Email = $"a-{suffix}@x", Name = "userA" };
        var userB = new User { Id = Guid.NewGuid(), Email = $"b-{suffix}@x", Name = "userB" };
        var teamA = new Team { Id = Guid.NewGuid(), Slug = $"a-{suffix}", Name = "TeamA", OwnerUserId = userA.Id };
        var teamB = new Team { Id = Guid.NewGuid(), Slug = $"b-{suffix}", Name = "TeamB", OwnerUserId = userB.Id };
        var projectA = TestProjectSeed.BuildDefaultProject(teamA.Id, userA.Id);
        var projectB = TestProjectSeed.BuildDefaultProject(teamB.Id, userB.Id);

        db.User.AddRange(userA, userB);
        db.Team.AddRange(teamA, teamB);
        db.Project.AddRange(projectA, projectB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (userA.Id, teamA.Id, teamB.Id);
    }

    private async Task<(Guid UserA, Guid TeamA, Guid TeamB, Guid InstanceB)> SeedTwoTeamsWithInstanceAsync()
    {
        var (userA, teamA, teamB) = await SeedTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceB = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamB,
            Provider = ProviderKind.Git,
            DisplayName = "B-instance",
            BaseUrl = $"https://b-{Guid.NewGuid():N}.local"
        };
        db.ProviderInstance.Add(instanceB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (userA, teamA, teamB, instanceB.Id);
    }

    private async Task<(Guid UserA, Guid TeamA, Guid TeamB, Guid InstanceB, Guid CredentialB)> SeedTwoTeamsWithCredentialAsync()
    {
        var (userA, teamA, teamB, instanceB) = await SeedTwoTeamsWithInstanceAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var credentialB = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamB,
            ProviderInstanceId = instanceB,
            AuthType = AuthType.Pat,
            DisplayName = "B-pat",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "secret-B" }))
        };
        db.Credential.Add(credentialB);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (userA, teamA, teamB, instanceB, credentialB.Id);
    }

    private async Task<(Guid UserA, Guid TeamA, Guid RepoB)> SeedTwoTeamsWithBoundRepoAsync()
    {
        var (userA, teamA, teamB, instanceB, credentialB) = await SeedTwoTeamsWithCredentialAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var projectB = await db.Project.AsNoTracking().Where(p => p.TeamId == teamB).Select(p => p.Id).SingleAsync().ConfigureAwait(false);

        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            TeamId = teamB,
            ProjectId = projectB,
            ProviderInstanceId = instanceB,
            CredentialId = credentialB,
            ExternalId = "id-B",
            NamespacePath = "teamB",
            Name = "repo",
            FullPath = "teamB/repo",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://test.local/teamB/repo",
            Status = RepositoryStatus.Active
        };
        db.Repository.Add(repo);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (userA, teamA, repo.Id);
    }
}
