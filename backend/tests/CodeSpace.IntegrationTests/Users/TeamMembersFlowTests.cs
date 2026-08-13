using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Users;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Users;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Users;

/// <summary>
/// Contract for the team-member directory (<see cref="IUserService.ListTeamMembersAsync"/>) and its
/// mediator path. The roster is the team's membership rows, owner included — it used to union them
/// with a separately-stored owner and dedupe, which is what made a one-person team read as two. This
/// is the identity source the chat UI uses to name message authors and drive @-mentions.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamMembersFlowTests
{
    private readonly PostgresFixture _fixture;

    public TeamMembersFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Lists_everyone_in_the_team_once_name_sorted()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (teamId, ownerId) = await SeedTeamAsync(suffix, ownerName: "Zoe");
        await AddMemberAsync(teamId, $"bob-{suffix}@x", "Bob");
        await AddMemberAsync(teamId, $"amy-{suffix}@x", "Amy");

        var members = await ListAsync(teamId);

        members.Select(m => m.Name).ShouldBe(new[] { "Amy", "Bob", "Zoe" }, customMessage: "the owner is on the roster like anyone else, and the list is name-sorted");
        members.Count(m => m.UserId == ownerId).ShouldBe(1, customMessage: "the owner is one person, counted once");
    }

    [Fact]
    public async Task Excludes_members_of_other_teams()
    {
        var a = Guid.NewGuid().ToString("N")[..8];
        var b = Guid.NewGuid().ToString("N")[..8];
        var (teamA, ownerA) = await SeedTeamAsync(a, ownerName: "A-Owner");
        var (teamB, _) = await SeedTeamAsync(b, ownerName: "B-Owner");
        await AddMemberAsync(teamB, $"b-{b}@x", "B-Member");

        var members = await ListAsync(teamA);

        members.ShouldHaveSingleItem().UserId.ShouldBe(ownerA);
    }

    /// <summary>
    /// <c>team_membership</c> is a hard-delete junction table, so its rows outlive a soft-deleted
    /// team. Reading them without asking about the team would keep answering for one that is gone.
    /// </summary>
    [Fact]
    public async Task A_closed_team_lists_nobody()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (teamId, _) = await SeedTeamAsync(suffix, ownerName: "Owner");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.Team.FindAsync(teamId))!.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        (await ListAsync(teamId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Excludes_soft_deleted_users()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (teamId, _) = await SeedTeamAsync(suffix, ownerName: "Owner");
        var goneId = await AddMemberAsync(teamId, $"gone-{suffix}@x", "Gone");

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var gone = await db.User.FindAsync(goneId);
            gone!.DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var members = await ListAsync(teamId);

        members.ShouldNotContain(m => m.UserId == goneId, customMessage: "A soft-deleted user must not appear in the member directory.");
    }

    [Fact]
    public async Task Unknown_team_returns_empty()
    {
        (await ListAsync(Guid.NewGuid())).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListTeamMembers_through_mediator_scopes_to_the_current_team()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (teamId, ownerId) = await SeedTeamAsync(suffix, ownerName: "Owner");
        var memberId = await AddMemberAsync(teamId, $"m-{suffix}@x", "Member");

        using var scope = _fixture.BeginScopeAs(ownerId, teamId);
        var members = await scope.Resolve<IMediator>().Send(new ListTeamMembersQuery());

        members.Select(m => m.UserId).ShouldBe(new[] { ownerId, memberId }, ignoreOrder: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CodeSpace.Messages.Dtos.Users.TeamMemberSummary>> ListAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IUserService>().ListTeamMembersAsync(teamId, default);
    }

    private async Task<(Guid TeamId, Guid OwnerId)> SeedTeamAsync(string suffix, string ownerName)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ownerId = Guid.NewGuid();
        db.User.Add(new User { Id = ownerId, Email = $"owner-{suffix}@x", Name = ownerName });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"tm-{suffix}", Name = "Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = ownerId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, ownerId);
    }

    private async Task<Guid> AddMemberAsync(Guid teamId, string email, string name)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = email, Name = name });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Member });

        await db.SaveChangesAsync();
        return userId;
    }
}
