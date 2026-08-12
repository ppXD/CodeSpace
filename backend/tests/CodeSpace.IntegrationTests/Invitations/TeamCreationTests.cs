using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Teams;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Invitations;

/// <summary>
/// Opening a workspace, and the instance capability that gates it.
///
/// <para>This is the first thing in the product that is neither "anyone signed in may" nor "a team
/// role decides" — there is no team yet to hold the role. The tests are written around that seam:
/// what an ungranted account gets, what a granted one gets, and that the team a creator ends up with
/// is one they can actually administer.</para>
///
/// <para>Grants are supplied to <c>ICurrentUser</c> directly here, because the integration harness
/// fakes identity wholesale — <c>TestCurrentUser</c> never reads the database, so a
/// <c>user_permission</c> row means nothing to it. That the real join actually grants the capability
/// is a different claim, and it is proved on the wire in <c>TeamCreationEndpointE2ETests</c> where
/// the real <c>ApiUser</c> runs.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamCreationTests
{
    private readonly PostgresFixture _fixture;

    public TeamCreationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_ordinary_account_cannot_open_a_workspace()
    {
        // On a shared deployment, "anyone signed in may create unlimited teams" is a resource
        // decision rather than an obvious default.
        var userId = await SeedUserAsync(withPermission: false);

        using var scope = _fixture.BeginScopeAs(userId, teamId: null);
        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamCommand { Name = "Uninvited" }).ConfigureAwait(false);

        var denied = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        denied.Reason.ShouldContain(Permissions.TeamsCreate);
    }

    [Fact]
    public async Task A_granted_account_opens_one_and_owns_it()
    {
        // The grant is per ACCOUNT, in data — which is the whole reason this tier exists rather than
        // being a team role.
        var userId = await SeedUserAsync(withPermission: true);

        using var scope = BeginScopeGranted(userId);
        var team = await scope.Resolve<IMediator>().Send(new CreateTeamCommand { Name = "Platform Team" }).ConfigureAwait(false);

        team.Slug.ShouldBe("platform-team");
        team.Kind.ShouldBe(TeamKind.Workspace);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.Team.Where(t => t.Id == team.Id).Select(t => t.OwnerUserId).SingleAsync().ConfigureAwait(false)).ShouldBe(userId);

        // Ownership is recorded twice on purpose: the roster, the role tier and the last-owner guard
        // all read the membership row, so a team created without one shows an empty member list and
        // refuses its own creator a role.
        var membership = await db.TeamMembership.SingleAsync(m => m.TeamId == team.Id && m.UserId == userId).ConfigureAwait(false);
        membership.Role.ShouldBe(TeamRole.Owner);
    }

    [Fact]
    public async Task The_creator_can_immediately_administer_what_they_made()
    {
        // The point of the previous test, proved through the product rather than the schema: if the
        // Owner membership row were missing, this would be refused for lack of members.manage.
        var userId = await SeedUserAsync(withPermission: true);

        using var scope = BeginScopeGranted(userId);
        var team = await scope.Resolve<IMediator>().Send(new CreateTeamCommand { Name = "Self Administered" }).ConfigureAwait(false);

        using var asTeam = _fixture.BeginScopeAs(userId, team.Id);
        var invite = await asTeam.Resolve<IMediator>().Send(new Messages.Commands.Invitations.CreateTeamInvitationCommand { Email = $"first-{Guid.NewGuid():N}@x", Role = TeamRole.Admin }).ConfigureAwait(false);

        invite.InviteUrl.ShouldContain("/invite/");
    }

    [Fact]
    public async Task The_admin_role_carries_the_capability_without_being_granted_it_separately()
    {
        // Migration 0115 grants teams.create to the Admin role, and the behavior short-circuits for
        // Admin anyway — asserted so a deployment cannot end up where nobody can grant anything.
        var userId = await SeedUserAsync(withPermission: false);

        using var scope = _fixture.BeginScopeAs(userId, teamId: null, Roles.Admin);
        var team = await scope.Resolve<IMediator>().Send(new CreateTeamCommand { Name = "Admin Made" }).ConfigureAwait(false);

        team.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Two_teams_of_the_same_name_both_get_a_usable_slug()
    {
        // Two teams called "Platform" is an ordinary thing to want. The second becoming platform-2
        // beats an error the person works around by inventing a different name.
        var userId = await SeedUserAsync(withPermission: true);

        using var scope = BeginScopeGranted(userId);
        var mediator = scope.Resolve<IMediator>();

        var first = await mediator.Send(new CreateTeamCommand { Name = "Duplicate Name" }).ConfigureAwait(false);
        var second = await mediator.Send(new CreateTeamCommand { Name = "Duplicate Name" }).ConfigureAwait(false);

        first.Slug.ShouldBe("duplicate-name");
        second.Slug.ShouldNotBe(first.Slug);
        second.Name.ShouldBe(first.Name, "only the slug is disambiguated — the display name is what the person typed");
    }

    [Fact]
    public async Task A_name_that_slugifies_to_nothing_still_produces_a_team()
    {
        // An all-punctuation name is not an error; it just cannot contribute to a URL.
        var userId = await SeedUserAsync(withPermission: true);

        using var scope = BeginScopeGranted(userId);
        var team = await scope.Resolve<IMediator>().Send(new CreateTeamCommand { Name = "!!!" }).ConfigureAwait(false);

        team.Slug.ShouldNotBeNullOrWhiteSpace();
        team.Name.ShouldBe("!!!");
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using var scope = _fixture.BeginScopeAs(userId: null, teamId: null);
        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamCommand { Name = "Nobody" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<UnauthorizedAccessException>().ConfigureAwait(false);
    }

    /// <summary>
    /// A scope whose ICurrentUser reports the capability. The harness's double never reads the
    /// database, so the row seeded below is inert here — see the class note.
    /// </summary>
    private Autofac.ILifetimeScope BeginScopeGranted(Guid userId) =>
        _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "creator") { Permissions = new[] { Permissions.TeamsCreate } }).As<Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(null)).As<Core.Services.Identity.ICurrentTeam>().SingleInstance();
        });

    /// <summary>
    /// Grants the capability the way an operator would: a <c>user_permission</c> row against the
    /// account, not a role. That path is the reason this tier lives in data at all, so it is the one
    /// the tests exercise.
    /// </summary>
    private async Task<Guid> SeedUserAsync(bool withPermission)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var user = new User { Id = Guid.NewGuid(), Email = $"creator-{Guid.NewGuid():N}@x", Name = "Creator" };
        db.User.Add(user);

        if (withPermission)
        {
            var permissionId = await db.Permission.AsNoTracking().Where(p => p.Name == Permissions.TeamsCreate).Select(p => p.Id).SingleAsync().ConfigureAwait(false);
            db.UserPermission.Add(new UserPermission { Id = Guid.NewGuid(), UserId = user.Id, PermissionId = permissionId });
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return user.Id;
    }
}
