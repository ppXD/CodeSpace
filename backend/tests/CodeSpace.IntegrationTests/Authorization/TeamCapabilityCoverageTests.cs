using System.Runtime.CompilerServices;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// Proves the role tier holds across EVERY route to a capability, not just the one a test author
/// happened to pick.
///
/// <para>The failure this exists to catch is a partial one: gate <c>CancelRunCommand</c> and leave
/// <c>ResumeRunCommand</c> open, and a suite full of green assertions about cancelling says nothing
/// true about run control. Aggregate-level authorization only means anything if it is total, so these
/// tests enumerate the commands from the assembly rather than from a list — a new write that reaches
/// an old capability is covered the moment it compiles, and cannot be forgotten.</para>
///
/// <para>Only the DENY direction is exhaustive, deliberately. Denial is decided by a pipeline
/// behavior before any handler is resolved, so dispatching an uninitialized command is inert — no
/// handler runs, nothing is written, nothing is called. Asserting the allow direction the same way
/// would run 60 handlers against null inputs, and a green run would mean the handlers tolerated
/// garbage rather than that the gate opened. The allow direction is pinned on real commands with real
/// payloads in <see cref="TeamPermissionEnforcementTests"/>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamCapabilityCoverageTests
{
    private readonly PostgresFixture _fixture;

    public TeamCapabilityCoverageTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_viewer_is_refused_every_permission_gated_write_in_the_product()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var allowed = await RefusalSurvivorsAsync(team.TeamId, team.Viewer, GatedWrites()).ConfigureAwait(false);

        allowed.ShouldBeEmpty(
            $"a Viewer reached {allowed.Count} write(s) the matrix denies them. Viewer is the read-only role, so any " +
            "entry here is a capability that leaked:\n  " + string.Join("\n  ", allowed));
    }

    [Fact]
    public async Task A_member_is_refused_every_admin_tier_write_in_the_product()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var adminTier = GatedWrites().Where(w => TeamRoleRank.Of(TeamPermissionMatrix.MinimumRoleFor(w.Permission)) > TeamRoleRank.Member).ToList();

        adminTier.ShouldNotBeEmpty("no admin-tier write was discovered — this assertion would pass vacuously");

        var allowed = await RefusalSurvivorsAsync(team.TeamId, team.Member, adminTier).ConfigureAwait(false);

        allowed.ShouldBeEmpty(
            "a plain Member reached an Admin-tier write. These are the credential, repository, and model-configuration " +
            $"capabilities the matrix reserves:\n  " + string.Join("\n  ", allowed));
    }

    [Fact]
    public async Task A_non_member_is_refused_every_permission_gated_write_in_the_product()
    {
        // The permission tier must never be reachable as a way IN — an outsider has to fail on
        // membership first, whatever the matrix would have said about their role.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var allowed = await RefusalSurvivorsAsync(team.TeamId, Guid.NewGuid(), GatedWrites()).ConfigureAwait(false);

        allowed.ShouldBeEmpty("a user with no membership in the team reached a team write:\n  " + string.Join("\n  ", allowed));
    }

    [Theory]
    [InlineData(TeamPermissions.RunsLaunch, "start a run")]
    [InlineData(TeamPermissions.RunsControl, "steer a running run")]
    [InlineData(TeamPermissions.RunsDecide, "answer for a human")]
    [InlineData(TeamPermissions.ModelsManage, "change what the team's model calls cost or where they go")]
    [InlineData(TeamPermissions.CredentialsManage, "mint or revoke a provider credential")]
    [InlineData(TeamPermissions.ReposManage, "change which repositories the team can reach")]
    public async Task Every_route_to_a_capability_is_closed_to_a_viewer(string permission, string capability)
    {
        // Named per capability so a failure says which POWER leaked, not which class did. The route
        // count is asserted so that deleting the last sibling doesn't silently empty the check.
        var routes = GatedWrites().Where(w => w.Permission == permission).ToList();

        routes.ShouldNotBeEmpty($"no command claims '{permission}', so nothing verifies a Viewer cannot {capability}");

        var team = await SeedTeamAsync().ConfigureAwait(false);
        var allowed = await RefusalSurvivorsAsync(team.TeamId, team.Viewer, routes).ConfigureAwait(false);

        allowed.ShouldBeEmpty($"a Viewer can still {capability} through {allowed.Count} of the {routes.Count} route(s) that reach it:\n  " + string.Join("\n  ", allowed));
    }

    [Fact]
    public async Task Answering_a_decision_and_clicking_its_chat_card_are_gated_alike()
    {
        // The two surfaces resolve to the same durable grain: AnswerDecisionCommand from the queue,
        // RespondToMessageCommand from the interactive card. Gating one and not the other leaves the
        // gate decorative, and the card is also how an agent's parked tool call gets approved.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        var surfaces = GatedWrites().Where(w => w.Type.Name is "AnswerDecisionCommand" or "RespondToMessageCommand").ToList();

        surfaces.Count.ShouldBe(2, "both decision surfaces must be permission-gated — found: " + string.Join(", ", surfaces.Select(s => s.Type.Name)));
        surfaces.Select(s => s.Permission).Distinct().ShouldHaveSingleItem();

        var allowed = await RefusalSurvivorsAsync(team.TeamId, team.Viewer, surfaces).ConfigureAwait(false);

        allowed.ShouldBeEmpty("a Viewer can answer a decision through:\n  " + string.Join("\n  ", allowed));
    }

    [Fact]
    public async Task A_viewer_can_still_reach_the_writes_that_only_touch_their_own_state()
    {
        // The mirror of every assertion above: gating is not the goal, correct gating is. A Viewer who
        // cannot mark a conversation read has been locked out of their own account, not secured.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var selfService = RequestTypes().Where(t => typeof(IRequireTeamMembership).IsAssignableFrom(t) && !typeof(IRequireTeamPermission).IsAssignableFrom(t)).ToList();

        selfService.ShouldNotBeEmpty("no self-service write was discovered — this assertion would pass vacuously");

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var mediator = scope.Resolve<IMediator>();

        foreach (var type in selfService)
        {
            var thrown = await Record.ExceptionAsync(() => mediator.Send(Probe(type))).ConfigureAwait(false);

            thrown.ShouldNotBeOfType<TenantAccessDeniedException>($"{type.Name} mutates only the caller's own state and must stay open to a Viewer");
        }
    }

    /// <summary>
    /// Dispatches each write as the given user and returns the ones that were NOT refused. Any other
    /// exception counts as refused-at-the-gate having not happened yet — the handler was reached, which
    /// for these inputs means it failed on a null field, and that is the outcome we want to see.
    /// </summary>
    private async Task<IReadOnlyList<string>> RefusalSurvivorsAsync(Guid teamId, Guid userId, IReadOnlyList<GatedWrite> writes)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var mediator = scope.Resolve<IMediator>();
        var survivors = new List<string>();

        foreach (var write in writes)
        {
            var thrown = await Record.ExceptionAsync(() => mediator.Send(Probe(write.Type))).ConfigureAwait(false);

            if (thrown is not TenantAccessDeniedException) survivors.Add($"{write.Type.Name} (needs '{write.Permission}') → {thrown?.GetType().Name ?? "no exception"}");
        }

        return survivors.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private sealed record GatedWrite(Type Type, string Permission);

    private static IReadOnlyList<GatedWrite> GatedWrites()
    {
        var writes = RequestTypes()
            .Where(t => typeof(IRequireTeamPermission).IsAssignableFrom(t))
            .Select(t => new GatedWrite(t, ((IRequireTeamPermission)Probe(t)).RequiredPermission))
            .ToList();

        writes.ShouldNotBeEmpty("the reflection scan found no permission-gated writes — every check in this class would pass vacuously");

        return writes;
    }

    private static IEnumerable<Type> RequestTypes() =>
        typeof(TeamPermissions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));

    /// <summary>
    /// These records carry required members, so they cannot be constructed generically. Authorization
    /// is decided before any handler is resolved, so a zeroed instance is enough to exercise the gate
    /// and never reaches code that would read the fields.
    /// </summary>
    private static object Probe(Type type) => RuntimeHelpers.GetUninitializedObject(type);

    private async Task<(Guid TeamId, Guid Member, Guid Viewer)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"own-{suffix}@x", Name = "owner" };
        var member = new User { Id = Guid.NewGuid(), Email = $"mem-{suffix}@x", Name = "member" };
        var viewer = new User { Id = Guid.NewGuid(), Email = $"vie-{suffix}@x", Name = "viewer" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"cap-{suffix}", Name = "Capability", OwnerUserId = owner.Id };

        db.User.AddRange(owner, member, viewer);
        db.Team.Add(team);
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, owner.Id));
        db.TeamMembership.AddRange(
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = member.Id, Role = TeamRole.Member },
            new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = viewer.Id, Role = TeamRole.Viewer });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (team.Id, member.Id, viewer.Id);
    }
}
