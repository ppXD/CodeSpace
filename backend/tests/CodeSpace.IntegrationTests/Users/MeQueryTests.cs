using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Users;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Users;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MeQueryTests
{
    private readonly PostgresFixture _fixture;

    public MeQueryTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Me_returns_user_with_owned_and_member_teams()
    {
        var (userId, ownedTeamId, memberTeamId) = await SeedUserWithTwoTeamsAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userId, ownedTeamId);
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new MeQuery()).ConfigureAwait(false);

        result.Id.ShouldBe(userId);
        result.Teams.Count.ShouldBe(2);

        var owned = result.Teams.Single(t => t.Id == ownedTeamId);
        owned.Role.ShouldBe(TeamRole.Owner);

        var member = result.Teams.Single(t => t.Id == memberTeamId);
        member.Role.ShouldBe(TeamRole.Member);
    }

    [Fact]
    public async Task Me_returns_zero_teams_for_user_with_no_memberships()
    {
        var userId = await SeedUserOnlyAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userId, teamId: null);
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new MeQuery()).ConfigureAwait(false);

        result.Id.ShouldBe(userId);
        result.Teams.ShouldBeEmpty();
    }

    [Fact]
    public async Task Me_team_counts_reflect_membership_and_repository_rows()
    {
        var (userId, teamId) = await SeedTeamWithDataAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new MeQuery()).ConfigureAwait(false);

        var team = result.Teams.Single(t => t.Id == teamId);
        team.RepositoryCount.ShouldBe(2);
        team.MemberCount.ShouldBe(2); // owner + 1 membership
        team.WorkflowCount.ShouldBe(2); // 2 active, 1 soft-deleted excluded
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedUserOnlyAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{Guid.NewGuid():N}@x", Name = "loner" };
        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return user.Id;
    }

    private async Task<(Guid UserId, Guid OwnedTeamId, Guid MemberTeamId)> SeedUserWithTwoTeamsAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var otherOwner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "other" };
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "subject" };

        var owned = new Team { Id = Guid.NewGuid(), Slug = $"owned-{suffix}", Name = "OwnedTeam", OwnerUserId = user.Id };
        var memberOf = new Team { Id = Guid.NewGuid(), Slug = $"member-{suffix}", Name = "MemberTeam", OwnerUserId = otherOwner.Id };

        db.User.AddRange(otherOwner, user);
        db.Team.AddRange(owned, memberOf);

        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = memberOf.Id, UserId = user.Id, Role = TeamRole.Member });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, owned.Id, memberOf.Id);
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamWithDataAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "owner" };
        var member = new User { Id = Guid.NewGuid(), Email = $"m-{suffix}@x", Name = "member" };

        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "BusyTeam", OwnerUserId = user.Id };
        var project = TestProjectSeed.BuildDefaultProject(team.Id, user.Id);
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.GitLab,
            DisplayName = "i",
            BaseUrl = $"https://gitlab-{suffix}.local"
        };

        db.User.AddRange(user, member);
        db.Team.Add(team);
        db.Project.Add(project);
        db.ProviderInstance.Add(instance);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member.Id, Role = TeamRole.Member });
        db.Repository.AddRange(
            new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, ExternalId = "r1", NamespacePath = "n", Name = "r1", FullPath = "n/r1", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "x", Status = RepositoryStatus.Active },
            new Repository { Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, ExternalId = "r2", NamespacePath = "n", Name = "r2", FullPath = "n/r2", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "x", Status = RepositoryStatus.Active });

        // Two active workflows + one soft-deleted — the count must include only the active two.
        db.Workflow.AddRange(
            new Workflow { Id = Guid.NewGuid(), TeamId = team.Id, Name = "wf-1", DefinitionJson = "{}" },
            new Workflow { Id = Guid.NewGuid(), TeamId = team.Id, Name = "wf-2", DefinitionJson = "{}" },
            new Workflow { Id = Guid.NewGuid(), TeamId = team.Id, Name = "wf-deleted", DefinitionJson = "{}", DeletedDate = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, team.Id);
    }
}
