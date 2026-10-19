using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// Who has standing in a team when the question is asked of someone other than the caller — the departed-token sweep, a
/// departure, a write held in someone's name, and the act-as picker and write all ask it. Everywhere else a live instance
/// Admin stands in every team as an Owner does (<see cref="TeamMembershipResolver"/>), so a membership row is not the
/// whole answer: an Admin who lent a team a token without joining it must not have it retired on deploy.
///
/// <para>Both forms the seams use are pinned to agree: the scalar ask, and the predicate composed into a query over rows
/// that name the team.</para>
///
/// <para>The role counts only while it is switched on, as the caller's own roles are read (<c>ApiUser.Roles</c>). Counted
/// regardless, a switched-off Admin role would still keep tokens from the sweep and pass the holds, the OAuth re-check and
/// act-as for someone the caller-side resolver already refuses.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TeamStandingTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private bool _adminRoleSwitchedOff;

    public TeamStandingTests(PostgresFixture fixture) { _fixture = fixture; }

    public enum Person { Member, InstanceAdminOutsideTheTeam, InstanceAdminInTheTeam, InstanceAdminOutsideTheTeamWithTheRoleSwitchedOff, ExInstanceAdminOutsideTheTeam, DepartedMember, AnotherTeamsMember, NoAccount }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// The Admin role is one row shared by the whole collection (its name is unique), so the case that switches it off
    /// switches it back on whatever its assertions did.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_adminRoleSwitchedOff) await SwitchAdminRoleAsync(on: true);
    }

    [Theory]
    [InlineData(Person.Member, true)]
    [InlineData(Person.InstanceAdminOutsideTheTeam, true)]
    [InlineData(Person.InstanceAdminInTheTeam, true)]
    [InlineData(Person.InstanceAdminOutsideTheTeamWithTheRoleSwitchedOff, false)]
    [InlineData(Person.ExInstanceAdminOutsideTheTeam, false)]
    [InlineData(Person.DepartedMember, false)]
    [InlineData(Person.AnotherTeamsMember, false)]
    [InlineData(Person.NoAccount, false)]
    public async Task Standing_is_a_membership_row_or_the_instance_admin_role(Person person, bool stands)
    {
        var (teamId, userId) = await SeedAsync(person);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var asked = await TeamStanding.HeldAsync(db, teamId, userId, CancellationToken.None);
        var composed = await db.Team.AsNoTracking().Where(t => t.Id == teamId).AnyAsync(TeamStanding.Of<Team>(db, t => t.Id, _ => userId));

        asked.ShouldBe(stands, $"{person}: {(stands ? "has" : "has no")} standing in the team");
        composed.ShouldBe(asked, $"{person}: the predicate a query composes answers exactly as the scalar ask does");
    }

    [Theory]
    [InlineData(Person.Member, TeamRole.Member)]
    [InlineData(Person.InstanceAdminOutsideTheTeam, TeamRole.Owner)]
    [InlineData(Person.InstanceAdminInTheTeam, TeamRole.Member)]   // acted ON, the row they hold is read before the role — the opposite of RoleHeldAsync, where someone ACTING stands as the Owner their role makes them
    [InlineData(Person.InstanceAdminOutsideTheTeamWithTheRoleSwitchedOff, null)]
    [InlineData(Person.ExInstanceAdminOutsideTheTeam, null)]
    [InlineData(Person.DepartedMember, null)]
    [InlineData(Person.AnotherTeamsMember, null)]
    [InlineData(Person.NoAccount, null)]
    public async Task The_role_a_person_is_acted_on_as_exists_exactly_when_they_stand_and_is_the_row_before_the_role(Person person, TeamRole? role)
    {
        var (teamId, userId) = await SeedAsync(person);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var actedOn = await TeamStanding.RoleActedOnAsync(db, teamId, userId, CancellationToken.None);
        var stands = await TeamStanding.HeldAsync(db, teamId, userId, CancellationToken.None);

        actedOn.ShouldBe(role, $"{person}");
        (actedOn is not null).ShouldBe(stands, $"{person}: someone is acted on as a role exactly when they stand in a live team");
    }

    [Fact]
    public async Task Many_people_are_read_in_one_ask_and_only_those_who_stand_are_named()
    {
        var (teamId, member) = await SeedAsync(Person.Member);
        var (_, outsider) = await SeedAsync(Person.NoAccount);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await TeamStanding.RolesActedOnAsync(db, teamId, new[] { member, outsider, Guid.NewGuid() }, CancellationToken.None)).ShouldBe(new Dictionary<Guid, TeamRole> { [member] = TeamRole.Member });
        (await TeamStanding.RolesActedOnAsync(db, teamId, Array.Empty<Guid>(), CancellationToken.None)).ShouldBeEmpty();
    }

    // ── World ─────────────────────────────────────────────────────────────────────

    private async Task<(Guid TeamId, Guid UserId)> SeedAsync(Person person)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"standing-{suffix}", Name = "Standing", Kind = TeamKind.Workspace };
        var otherTeam = new Team { Id = Guid.NewGuid(), Slug = $"standing-other-{suffix}", Name = "Elsewhere", Kind = TeamKind.Workspace };
        var user = new User { Id = Guid.NewGuid(), Email = $"standing-{suffix}@standing.test", Name = $"standing-{suffix}" };

        if (person == Person.NoAccount) return (await SeedTeamAsync(team), user.Id);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Team.AddRange(team, otherTeam);
        db.User.Add(user);

        if (person is Person.Member or Person.InstanceAdminInTheTeam or Person.DepartedMember) db.TeamMembership.Add(Membership(team, user));
        if (person == Person.AnotherTeamsMember) db.TeamMembership.Add(Membership(otherTeam, user));
        if (person is Person.InstanceAdminOutsideTheTeam or Person.InstanceAdminInTheTeam or Person.InstanceAdminOutsideTheTeamWithTheRoleSwitchedOff or Person.ExInstanceAdminOutsideTheTeam) db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = await AdminRoleIdAsync(db), UserId = user.Id });

        await db.SaveChangesAsync();

        if (person == Person.DepartedMember) await db.TeamMembership.Where(m => m.TeamId == team.Id && m.UserId == user.Id).ExecuteDeleteAsync();
        if (person == Person.ExInstanceAdminOutsideTheTeam) await db.RoleUser.Where(r => r.UserId == user.Id).ExecuteDeleteAsync();
        if (person == Person.InstanceAdminOutsideTheTeamWithTheRoleSwitchedOff) await SwitchAdminRoleAsync(on: false);

        return (team.Id, user.Id);
    }

    private async Task SwitchAdminRoleAsync(bool on)
    {
        _adminRoleSwitchedOff = !on;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await db.Role.Where(r => r.Name == Roles.Admin).ExecuteUpdateAsync(r => r.SetProperty(x => x.Status, on));
    }

    private async Task<Guid> SeedTeamAsync(Team team)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Team.Add(team);
        await db.SaveChangesAsync();

        return team.Id;
    }

    private static async Task<Guid> AdminRoleIdAsync(CodeSpaceDbContext db) => await db.Role.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync();

    private static TeamMembership Membership(Team team, User user) => new() { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = TeamRole.Member };
}
