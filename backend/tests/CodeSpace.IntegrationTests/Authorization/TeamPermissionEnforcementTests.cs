using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Decisions;
using CodeSpace.Messages.Commands.ModelCredentials;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>
/// Pins the team-role tier ON THE COMMANDS THAT HAVE ADOPTED IT. Membership decides whether you can
/// SEE the team; <c>team_membership.role</c> decides what you can DO in it — so far for five commands.
///
/// <para>Scope warning for anyone reading a green run as "role is enforced": it is not, yet. Most team
/// writes still pass on membership alone, including siblings that reach the same aggregate as the five
/// below — a Viewer denied <c>CancelRunCommand</c> can still resume the same run. What is verified here
/// is that the MECHANISM denies and permits correctly. <c>TeamWritePermissionAdoptionTests</c> counts
/// what it does not yet cover.</para>
///
/// <para>Allow-path assertions deliberately assert "not <see cref="TenantAccessDeniedException"/>"
/// rather than a successful result. The subject here is the authorization gate, and pinning each
/// handler's happy path would couple this file to four unrelated domains — a Viewer and a Member
/// hitting the same nonexistent run must fail differently, and that difference is the whole test.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamPermissionEnforcementTests
{
    private readonly PostgresFixture _fixture;

    public TeamPermissionEnforcementTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Viewer_cannot_launch_a_run()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new RunWorkflowManuallyCommand { WorkflowId = Guid.NewGuid() }).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        ex.Reason.ShouldContain(TeamPermissions.RunsLaunch);
    }

    [Fact]
    public async Task Viewer_can_still_read()
    {
        // Reads carry membership only and must stay that way — if this ever throws, the tier has
        // leaked onto the read side and Viewer has lost the access the role exists to grant.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var workflows = await mediator.Send(new ListWorkflowsQuery()).ConfigureAwait(false);

        workflows.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(TeamRole.Viewer, true)]
    [InlineData(TeamRole.Member, false)]
    [InlineData(TeamRole.Admin, false)]
    [InlineData(TeamRole.Owner, false)]
    public async Task Member_tier_writes_are_denied_to_Viewer_only(TeamRole role, bool denied)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.UserFor(role), team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var thrown = await Record.ExceptionAsync(() => mediator.Send(new CancelRunCommand { RunId = Guid.NewGuid() })).ConfigureAwait(false);

        AssertDenial(thrown, denied, role, TeamPermissions.RunsControl);
    }

    [Theory]
    [InlineData(TeamRole.Viewer, true)]
    [InlineData(TeamRole.Member, true)]
    [InlineData(TeamRole.Admin, false)]
    [InlineData(TeamRole.Owner, false)]
    public async Task Admin_tier_writes_are_denied_below_Admin(TeamRole role, bool denied)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.UserFor(role), team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var command = new AddModelCredentialCommand { Provider = "openai", DisplayName = $"probe-{Guid.NewGuid():N}", ApiKey = "sk-probe" };
        var thrown = await Record.ExceptionAsync(() => mediator.Send(command)).ConfigureAwait(false);

        AssertDenial(thrown, denied, role, TeamPermissions.ModelsManage);
    }

    [Fact]
    public async Task A_plain_member_can_no_longer_mint_a_model_credential()
    {
        // Narrows, does not close: UpdateModelCredentialCommand still takes a new ApiKey and BaseUrl on
        // membership alone, so a member can rewrite an existing credential even though they can no
        // longer add one. That sibling is on the pending list, not fixed here.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Member, team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new AddModelCredentialCommand { Provider = "openai", DisplayName = "sneaky", ApiKey = "sk-sneaky" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        db.ModelCredential.Any(c => c.DisplayName == "sneaky").ShouldBeFalse("the denied command must not have written anything");
    }

    [Fact]
    public async Task Viewer_cannot_answer_a_decision()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new AnswerDecisionCommand { DecisionId = Guid.NewGuid(), FreeText = "approve" }).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        ex.Reason.ShouldContain(TeamPermissions.RunsDecide);
    }

    [Fact]
    public async Task Viewer_cannot_create_a_workflow()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Viewer, team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var command = new CreateWorkflowCommand
        {
            Name = "denied",
            Definition = new WorkflowDefinition { Nodes = Array.Empty<NodeDefinition>(), Edges = Array.Empty<EdgeDefinition>() },
            Activations = Array.Empty<WorkflowActivationInput>()
        };
        var act = async () => await mediator.Send(command).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        ex.Reason.ShouldContain(TeamPermissions.WorkflowsWrite);
    }

    [Fact]
    public async Task The_global_Admin_role_still_bypasses()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), team.TeamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var thrown = await Record.ExceptionAsync(() => mediator.Send(new CancelRunCommand { RunId = Guid.NewGuid() })).ConfigureAwait(false);

        thrown.ShouldNotBeOfType<TenantAccessDeniedException>();
    }

    [Fact]
    public async Task A_non_member_is_still_refused_before_the_permission_check()
    {
        // The membership tier must keep failing first — an outsider gets "not a member", never a
        // permission verdict that would confirm the team exists.
        //
        // Also the whole of standing since 0118: the role lookup used to read team.owner_user_id too,
        // which handed Owner to whoever that column named whether or not they were still in the team.
        // The membership row is the only record now, so not having one is the end of the question.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new CancelRunCommand { RunId = Guid.NewGuid() }).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        ex.Reason.ShouldBe("user is not a member of this team");
    }

    private static void AssertDenial(Exception? thrown, bool denied, TeamRole role, string permission)
    {
        if (denied)
        {
            var denial = thrown.ShouldBeOfType<TenantAccessDeniedException>($"{role} must not hold '{permission}'");
            denial.Reason.ShouldContain(permission);
            return;
        }

        thrown.ShouldNotBeOfType<TenantAccessDeniedException>($"{role} holds '{permission}' and must clear the authorization gate");
    }

    private async Task<SeededTeam> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "owner" };
        var admin = new User { Id = Guid.NewGuid(), Email = $"admin-{suffix}@x", Name = "admin" };
        var member = new User { Id = Guid.NewGuid(), Email = $"member-{suffix}@x", Name = "member" };
        var viewer = new User { Id = Guid.NewGuid(), Email = $"viewer-{suffix}@x", Name = "viewer" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };

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
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unseeded role")
        };
    }
}
