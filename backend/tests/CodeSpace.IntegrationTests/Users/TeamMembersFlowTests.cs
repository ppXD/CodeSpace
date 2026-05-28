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
/// Contract for the team-member directory (<see cref="IUserService.ListTeamMembersAsync"/>) and
/// its mediator path. The owner is stored separately from membership rows (a team's owner has no
/// self-membership row in production — see MeQuery's <c>owner + memberships</c> count), so the
/// listing must union the two and dedupe. This is the identity source the chat UI uses to name
/// message authors and drive @-mentions.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamMembersFlowTests
{
    private readonly PostgresFixture _fixture;

    public TeamMembersFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Lists_the_owner_plus_membership_rows_name_sorted()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (teamId, _) = await SeedTeamAsync(suffix, ownerName: "Zoe", ownerHasMembershipRow: false);
        await AddMemberAsync(teamId, $"bob-{suffix}@x", "Bob");
        await AddMemberAsync(teamId, $"amy-{suffix}@x", "Amy");

        var members = await ListAsync(teamId);

        members.Select(m => m.Name).ShouldBe(new[] { "Amy", "Bob", "Zoe" },
            customMessage: "Owner (with no self-membership row) must appear, and the list is name-sorted.");
    }

    [Fact]
    public async Task Owner_with_a_self_membership_row_is_not_duplicated()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (teamId, ownerId) = await SeedTeamAsync(suffix, ownerName: "Owner", ownerHasMembershipRow: true);

        var members = await ListAsync(teamId);

        members.Count(m => m.UserId == ownerId).ShouldBe(1, customMessage: "An owner who also has a membership row must appear exactly once.");
    }

    [Fact]
    public async Task Excludes_members_of_other_teams()
    {
        var a = Guid.NewGuid().ToString("N")[..8];
        var b = Guid.NewGuid().ToString("N")[..8];
        var (teamA, ownerA) = await SeedTeamAsync(a, ownerName: "A-Owner", ownerHasMembershipRow: false);
        var (teamB, _) = await SeedTeamAsync(b, ownerName: "B-Owner", ownerHasMembershipRow: false);
        await AddMemberAsync(teamB, $"b-{b}@x", "B-Member");

        var members = await ListAsync(teamA);

        members.ShouldHaveSingleItem().UserId.ShouldBe(ownerA);
    }

    [Fact]
    public async Task Excludes_soft_deleted_users()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (teamId, _) = await SeedTeamAsync(suffix, ownerName: "Owner", ownerHasMembershipRow: false);
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
        var (teamId, ownerId) = await SeedTeamAsync(suffix, ownerName: "Owner", ownerHasMembershipRow: true);
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

    private async Task<(Guid TeamId, Guid OwnerId)> SeedTeamAsync(string suffix, string ownerName, bool ownerHasMembershipRow)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var ownerId = Guid.NewGuid();
        db.User.Add(new User { Id = ownerId, Email = $"owner-{suffix}@x", Name = ownerName });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"tm-{suffix}", Name = "Team", Kind = TeamKind.Workspace, OwnerUserId = ownerId });

        if (ownerHasMembershipRow)
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
