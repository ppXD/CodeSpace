using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Decisions;
using CodeSpace.Messages.Commands.ModelCredentials;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
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

    public enum ResourceWrite { RevokeCredential, UnbindRepository, RelinkRepositoryCredential, TestRepositoryBinding }

    public static IEnumerable<object[]> ResourceWritesByRole() =>
        from write in Enum.GetValues<ResourceWrite>()
        from role in new[] { TeamRole.Viewer, TeamRole.Member, TeamRole.Admin, TeamRole.Owner }
        select new object[] { write, role, TeamRoleRank.Of(role) < TeamRoleRank.Admin };

    /// <summary>
    /// Writes addressed by a credential or repository id carried only their resource marker, which checks the
    /// resource's team and the caller's membership — no role. A Viewer, refused when ADDING a credential, could
    /// revoke the Owner's, re-point or unbind a repository, and spend its credential on a provider probe. Real rows
    /// are seeded so the allow path reaches past every gate rather than failing on a missing resource.
    /// </summary>
    [Theory]
    [MemberData(nameof(ResourceWritesByRole))]
    public async Task Resource_keyed_credential_and_repository_writes_are_denied_below_Admin(ResourceWrite write, TeamRole role, bool denied)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var bound = await SeedBoundRepositoryAsync(team).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.UserFor(role), team.TeamId);
        var mediator = scope.Resolve<IMediator>();
        var thrown = await Record.ExceptionAsync(() => SendAsync(mediator, write, bound)).ConfigureAwait(false);

        AssertDenial(thrown, denied, role, write == ResourceWrite.RevokeCredential ? TeamPermissions.CredentialsManage : TeamPermissions.ReposManage);
    }

    public enum CredentialHolder { Caller, Teammate, Team }

    public static IEnumerable<object[]> RevokesByRoleAndHolder() =>
        from role in new[] { TeamRole.Viewer, TeamRole.Member, TeamRole.Admin, TeamRole.Owner }
        from holder in Enum.GetValues<CredentialHolder>()
        select new object[] { role, holder, TeamRoleRank.Of(role) < TeamRoleRank.Admin && holder != CredentialHolder.Caller };

    /// <summary>
    /// Any member can link a personal provider token of their own, so any member must be able to disconnect it again.
    /// Gating revoke on credentials.manage outright closed the Viewer-revokes-the-Owner's-credential hole and, with it,
    /// took that away from every Member and Viewer; a teammate's credential and the team's own still take Admin.
    /// </summary>
    [Theory]
    [MemberData(nameof(RevokesByRoleAndHolder))]
    public async Task Below_Admin_only_the_callers_own_personal_credential_can_be_revoked(TeamRole role, CredentialHolder holder, bool denied)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var caller = team.UserFor(role);
        var teammate = role == TeamRole.Owner ? team.Admin : team.Owner;
        var credentialId = await SeedCredentialAsync(team.TeamId, holder switch { CredentialHolder.Caller => caller, CredentialHolder.Teammate => teammate, _ => null }).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(caller, team.TeamId);
        var thrown = await Record.ExceptionAsync(() => scope.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credentialId })).ConfigureAwait(false);

        AssertDenial(thrown, denied, role, TeamPermissions.CredentialsManage);
        if (denied) return;

        thrown.ShouldBeNull();
        (await LoadCredentialStatusAsync(credentialId).ConfigureAwait(false)).ShouldBe(CredentialStatus.Revoked);
    }

    /// <summary>
    /// Listing what a credential reaches presents it to the provider, so on membership alone a Viewer could spend the
    /// Owner's personal token to enumerate everything the Owner's account can see there — private repositories outside
    /// the team included. Below Admin only the caller's own Personal credential lists; a teammate's and the team's own
    /// take repos.manage, the bar of the bind the listing feeds. The allow path reaches the provider lookup, which the Git
    /// kind cannot answer — anything but a denial is past the gate.
    /// </summary>
    [Theory]
    [MemberData(nameof(RevokesByRoleAndHolder))]
    public async Task Below_Admin_only_the_callers_own_personal_credential_lists_what_it_reaches(TeamRole role, CredentialHolder holder, bool denied)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var caller = team.UserFor(role);
        var teammate = role == TeamRole.Owner ? team.Admin : team.Owner;
        var credentialId = await SeedCredentialAsync(team.TeamId, holder switch { CredentialHolder.Caller => caller, CredentialHolder.Teammate => teammate, _ => null }).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(caller, team.TeamId);
        var thrown = await Record.ExceptionAsync(() => scope.Resolve<IMediator>().Send(new ListAccessibleRepositoriesQuery { CredentialId = credentialId })).ConfigureAwait(false);

        AssertDenial(thrown, denied, role, TeamPermissions.ReposManage);
    }

    [Fact]
    public async Task An_instance_Admin_outside_the_team_lists_through_a_members_personal_credential()
    {
        // The instance Admin role has an Owner's standing in every team (TeamStanding), membership row or not.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var credentialId = await SeedCredentialAsync(team.TeamId, team.Member).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), team.TeamId, Roles.Admin);
        var thrown = await Record.ExceptionAsync(() => scope.Resolve<IMediator>().Send(new ListAccessibleRepositoriesQuery { CredentialId = credentialId })).ConfigureAwait(false);

        thrown.ShouldNotBeOfType<TenantAccessDeniedException>();
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

    private static Task SendAsync(IMediator mediator, ResourceWrite write, BoundRepository bound) => write switch
    {
        ResourceWrite.RevokeCredential => mediator.Send(new RevokeCredentialCommand { CredentialId = bound.SpareCredentialId }),
        ResourceWrite.UnbindRepository => mediator.Send(new UnbindRepositoryCommand { RepositoryId = bound.RepositoryId }),
        ResourceWrite.RelinkRepositoryCredential => mediator.Send(new RelinkRepositoryCredentialCommand { RepositoryId = bound.RepositoryId, NewCredentialId = bound.SpareCredentialId }),
        _ => mediator.Send(new TestRepositoryBindingCommand { RepositoryId = bound.RepositoryId }),
    };

    private sealed record BoundRepository(Guid RepositoryId, Guid SpareCredentialId);

    /// <summary>A repository the Owner bound through one credential, plus a second credential of theirs on the same instance.</summary>
    private async Task<BoundRepository> SeedBoundRepositoryAsync(SeededTeam team)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.TeamId, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git-{suffix}.local" };
        var bound = new Credential { Id = Guid.NewGuid(), TeamId = team.TeamId, ProviderInstanceId = instance.Id, OwnerUserId = team.Owner, AuthType = AuthType.Pat, DisplayName = "bound", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-bound" })), Status = CredentialStatus.Active };
        var spare = new Credential { Id = Guid.NewGuid(), TeamId = team.TeamId, ProviderInstanceId = instance.Id, OwnerUserId = team.Owner, AuthType = AuthType.Pat, DisplayName = "spare", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-spare" })), Status = CredentialStatus.Active };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team.TeamId, ProviderInstanceId = instance.Id, CredentialId = bound.Id, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = $"api-{suffix}", FullPath = $"acme/api-{suffix}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://git-{suffix}.local/acme/api", Status = RepositoryStatus.Active };

        db.ProviderInstance.Add(instance);
        db.Credential.AddRange(bound, spare);
        db.Repository.Add(repository);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new BoundRepository(repository.Id, spare.Id);
    }

    /// <summary>A PAT on a fresh instance: Personal and owned by <paramref name="ownerUserId"/>, or the team's own service credential when it is null.</summary>
    private async Task<Guid> SeedCredentialAsync(Guid teamId, Guid? ownerUserId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "pat-revocable" }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git-{suffix}.local" };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, OwnerUserId = ownerUserId, Ownership = ownerUserId == null ? CredentialOwnership.TeamService : CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "revocable", EncryptedPayload = payload, Status = CredentialStatus.Active };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    private async Task<CredentialStatus> LoadCredentialStatusAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.Status).SingleAsync().ConfigureAwait(false);
    }

    private async Task<SeededTeam> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "owner" };
        var admin = new User { Id = Guid.NewGuid(), Email = $"admin-{suffix}@x", Name = $"admin-{suffix}" };
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
