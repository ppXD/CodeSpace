using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Users;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Teams;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Queries.Invitations;
using CodeSpace.Messages.Queries.Users;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Invitations;

/// <summary>
/// Changing who is in a team, against a real database and through the real pipeline.
///
/// <para>Two invariants carry most of these: nobody reaches past their own rank in either direction,
/// and a team always has an owner. The second is the one worth stating plainly — a team with no owner
/// has nobody who can transfer ownership, invite an owner, or delete it, so it is unrecoverable
/// rather than degraded, and every path out of ownership has to refuse.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamMemberManagementTests
{
    private readonly PostgresFixture _fixture;

    public TeamMemberManagementTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_admin_moves_a_member_between_roles()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        await SendAsAsync(team.Admin, team.TeamId, new ChangeTeamMemberRoleCommand { UserId = team.Viewer, Role = TeamRole.Member }).ConfigureAwait(false);

        (await RoleOfAsync(team.TeamId, team.Viewer).ConfigureAwait(false)).ShouldBe(TeamRole.Member);
    }

    [Fact]
    public async Task An_admin_cannot_promote_anyone_to_owner()
    {
        // Installing an owner is the one thing an Admin must not be able to do — including for
        // themselves, one hop later.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var act = async () => await SendAsAsync(team.Admin, team.TeamId, new ChangeTeamMemberRoleCommand { UserId = team.Member, Role = TeamRole.Owner }).ConfigureAwait(false);

        await act.ShouldThrowAsync<RoleOutranksActorException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task An_admin_cannot_touch_another_admin()
    {
        // Equal rank is not authority over. Otherwise two admins can demote each other, and whoever
        // clicks first wins.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var peer = await AddMemberAsync(team.TeamId, TeamRole.Admin).ConfigureAwait(false);

        var act = async () => await SendAsAsync(team.Admin, team.TeamId, new ChangeTeamMemberRoleCommand { UserId = peer, Role = TeamRole.Viewer }).ConfigureAwait(false);

        await act.ShouldThrowAsync<RoleOutranksActorException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task An_admin_cannot_demote_the_owner()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var act = async () => await SendAsAsync(team.Admin, team.TeamId, new ChangeTeamMemberRoleCommand { UserId = team.Owner, Role = TeamRole.Member }).ConfigureAwait(false);

        await act.ShouldThrowAsync<RoleOutranksActorException>().ConfigureAwait(false);
    }

    [Theory]
    [InlineData(TeamRole.Viewer)]
    [InlineData(TeamRole.Member)]
    public async Task Managing_members_needs_the_permission(TeamRole role)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var act = async () => await SendAsAsync(team.UserFor(role), team.TeamId, new RemoveTeamMemberCommand { UserId = team.Member }).ConfigureAwait(false);

        var denied = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        denied.Reason.ShouldContain(TeamPermissions.MembersManage);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var act = async () => await SendAsAsync(team.Owner, team.TeamId, new ChangeTeamMemberRoleCommand { UserId = team.Owner, Role = TeamRole.Admin }).ConfigureAwait(false);

        await act.ShouldThrowAsync<LastOwnerException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task The_last_owner_cannot_leave()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var act = async () => await SendAsAsync(team.Owner, team.TeamId, new LeaveTeamCommand()).ConfigureAwait(false);

        await act.ShouldThrowAsync<LastOwnerException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_second_owner_frees_the_first_to_go()
    {
        // The guard is about the team keeping an owner, not about a particular person being stuck.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var successor = await AddMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false);

        await SendAsAsync(team.Owner, team.TeamId, new LeaveTeamCommand()).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.TeamMembership.AnyAsync(m => m.TeamId == team.TeamId && m.UserId == team.Owner).ConfigureAwait(false)).ShouldBeFalse();
        (await db.TeamMembership.AnyAsync(m => m.TeamId == team.TeamId && m.UserId == successor && m.Role == TeamRole.Owner).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task Anyone_may_leave_a_team_they_were_added_to()
    {
        // A Viewer who cannot leave has been locked in, which is not what a read-only role is for —
        // so leaving carries no permission.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        await SendAsAsync(team.Viewer, team.TeamId, new LeaveTeamCommand()).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().TeamMembership.AnyAsync(m => m.TeamId == team.TeamId && m.UserId == team.Viewer).ConfigureAwait(false)).ShouldBeFalse();
    }

    [Fact]
    public async Task Transferring_ownership_moves_both_sides_at_once()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        await SendAsAsync(team.Owner, team.TeamId, new TransferTeamOwnershipCommand { ToUserId = team.Admin }).ConfigureAwait(false);

        (await RoleOfAsync(team.TeamId, team.Admin).ConfigureAwait(false)).ShouldBe(TeamRole.Owner);
        (await RoleOfAsync(team.TeamId, team.Owner).ConfigureAwait(false)).ShouldBe(TeamRole.Admin, "the outgoing owner stays, demoted — transferring is not leaving");
    }

    [Fact]
    public async Task Only_an_owner_may_transfer_ownership()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var act = async () => await SendAsAsync(team.Admin, team.TeamId, new TransferTeamOwnershipCommand { ToUserId = team.Member }).ConfigureAwait(false);

        var denied = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        denied.Reason.ShouldContain(TeamPermissions.TeamManage);
    }

    [Fact]
    public async Task The_roster_says_what_each_person_is()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var roster = await scope.Resolve<IMediator>().Send(new ListTeamMembersQuery()).ConfigureAwait(false);

        roster.Single(r => r.UserId == team.Owner).Role.ShouldBe(TeamRole.Owner);
        roster.Single(r => r.UserId == team.Admin).Role.ShouldBe(TeamRole.Admin);
        roster.Single(r => r.UserId == team.Viewer).Role.ShouldBe(TeamRole.Viewer);
        roster.Single(r => r.UserId == team.Admin).JoinedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_viewer_is_not_shown_who_has_been_invited()
    {
        // Who has been offered a seat, at what role, and by whom is management information about people
        // who are not in the team yet.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var act = async () => await scope.Resolve<IMediator>().Send(new ListTeamInvitationsQuery()).ConfigureAwait(false);

        var denied = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        denied.Reason.ShouldContain(TeamPermissions.MembersManage);
    }

    [Fact]
    public async Task Me_carries_exactly_what_the_caller_may_do()
    {
        // So a client can hide what it must not offer without keeping a second copy of the matrix. The
        // set is asserted against the matrix itself rather than a literal list, because a literal here
        // would BE the second copy.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var me = await scope.Resolve<IMediator>().Send(new MeQuery()).ConfigureAwait(false);

        var viewerTeam = me.Teams.Single(t => t.Id == team.TeamId);
        viewerTeam.Role.ShouldBe(TeamRole.Viewer);
        viewerTeam.Permissions.ShouldBeEmpty("Viewer is the read-only role — anything here is a capability the matrix says they do not have");

        using var ownerScope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var ownerMe = await ownerScope.Resolve<IMediator>().Send(new MeQuery()).ConfigureAwait(false);

        ownerMe.Teams.Single(t => t.Id == team.TeamId).Permissions.ShouldBe(Messages.Authorization.TeamPermissionMatrix.GrantedTo(TeamRole.Owner));
    }

    // ── Ownership ends where the Owner row ends ────────────────────────────────────
    //
    // Ownership used to be recorded on `team.owner_user_id` as well, and that column outlived every
    // way of ending it: leaving and being removed delete the membership row and cannot delete a
    // column, and a demotion rewrote a row the column then overruled. Each of these three drives one
    // of those exits and then asks what the person can still do.
    //
    // Each drives the SEEDED owner on purpose, because that is the account the column named. Against a
    // build that still consults it, all three fail: the person is out of the team and reads as its
    // Owner anyway.

    [Fact]
    public async Task Leaving_a_team_ends_ownership_of_it()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        await AddMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false);

        await SendAsAsync(team.Owner, team.TeamId, new LeaveTeamCommand()).ConfigureAwait(false);

        await ShouldHaveNoStandingAsync(team.TeamId, team.Owner).ConfigureAwait(false);
    }

    [Fact]
    public async Task Being_removed_from_a_team_ends_ownership_of_it()
    {
        // Stepped down first because equal rank is not authority over: an Owner cannot remove another
        // Owner, so somebody has to stop being one before they can be removed at all.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var successor = await AddMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false);

        await SendAsAsync(team.Owner, team.TeamId, new ChangeTeamMemberRoleCommand { UserId = team.Owner, Role = TeamRole.Admin }).ConfigureAwait(false);
        await SendAsAsync(successor, team.TeamId, new RemoveTeamMemberCommand { UserId = team.Owner }).ConfigureAwait(false);

        await ShouldHaveNoStandingAsync(team.TeamId, team.Owner).ConfigureAwait(false);
    }

    [Fact]
    public async Task Being_demoted_ends_ownership_of_the_team()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        await AddMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false);

        await SendAsAsync(team.Owner, team.TeamId, new ChangeTeamMemberRoleCommand { UserId = team.Owner, Role = TeamRole.Viewer }).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var me = await scope.Resolve<IMediator>().Send(new MeQuery()).ConfigureAwait(false);

        me.Teams.Single(t => t.Id == team.TeamId).Role.ShouldBe(TeamRole.Viewer, "a demotion that leaves the person able to act as Owner is decoration");
        me.Teams.Single(t => t.Id == team.TeamId).Permissions.ShouldBeEmpty();

        var act = async () => await SendAsAsync(team.Owner, team.TeamId, new RemoveTeamMemberCommand { UserId = team.Member }).ConfigureAwait(false);
        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
    }

    // ── Drivers ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing left: not a role the tenancy pipeline will grant, not a line on the roster, not a team
    /// in their own sidebar. Asked three ways because ownership was read from three places.
    /// </summary>
    private async Task ShouldHaveNoStandingAsync(Guid teamId, Guid userId)
    {
        var act = async () => await SendAsAsync(userId, teamId, new ListTeamMembersQuery()).ConfigureAwait(false);
        var denied = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        denied.Reason.ShouldContain("not a member");

        using var scope = _fixture.BeginScope();
        var roster = await scope.Resolve<IUserService>().ListTeamMembersAsync(teamId, default).ConfigureAwait(false);
        roster.ShouldNotContain(r => r.UserId == userId, "the roster still lists someone who is no longer in the team");

        using var theirs = _fixture.BeginScopeAs(userId, teamId);
        var me = await theirs.Resolve<IUserService>().BuildMeForAsync(await UserAsync(userId).ConfigureAwait(false), default).ConfigureAwait(false);
        me.Teams.ShouldNotContain(t => t.Id == teamId, "the team is still in the sidebar of someone who left it");
    }

    private async Task<User> UserAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().User.AsNoTracking().SingleAsync(u => u.Id == userId).ConfigureAwait(false);
    }

    private async Task SendAsAsync<T>(Guid userId, Guid teamId, IRequest<T> request)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);

        await scope.Resolve<IMediator>().Send(request).ConfigureAwait(false);
    }

    private async Task<TeamRole?> RoleOfAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.UserId == userId)
            .Select(m => (TeamRole?)m.Role)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private async Task<Guid> AddMemberAsync(Guid teamId, TeamRole role)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"extra-{Guid.NewGuid():N}@x", Name = $"extra-{role}" };

        db.User.Add(user);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync().ConfigureAwait(false);

        return user.Id;
    }

    private async Task<SeededTeam> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"own-{suffix}@x", Name = "owner" };
        var admin = new User { Id = Guid.NewGuid(), Email = $"adm-{suffix}@x", Name = "admin" };
        var member = new User { Id = Guid.NewGuid(), Email = $"mem-{suffix}@x", Name = "member" };
        var viewer = new User { Id = Guid.NewGuid(), Email = $"vie-{suffix}@x", Name = "viewer" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"mem-{suffix}", Name = "Members" };

        db.User.AddRange(owner, admin, member, viewer);
        db.Team.Add(team);
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, owner.Id));
        db.TeamMembership.AddRange(
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = admin.Id, Role = TeamRole.Admin },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member.Id, Role = TeamRole.Member },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = viewer.Id, Role = TeamRole.Viewer });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeededTeam(team.Id, owner.Id, admin.Id, member.Id, viewer.Id);
    }

    private sealed record SeededTeam(Guid TeamId, Guid Owner, Guid Admin, Guid Member, Guid Viewer)
    {
        public Guid UserFor(TeamRole role) => role switch
        {
            TeamRole.Owner => Owner,
            TeamRole.Admin => Admin,
            TeamRole.Member => Member,
            TeamRole.Viewer => Viewer,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unseeded role"),
        };
    }
}
