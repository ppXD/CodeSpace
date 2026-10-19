using Autofac;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// 🟢 High fidelity — what a remedy says and offers, for each person who might be looking, read from real rows over real Postgres:
/// the real reader of a run's recorded failure, the real membership resolver and the real permission matrix. The failures are
/// written by the real <see cref="IRunRecordLogger"/> from the real exceptions, so the shapes cannot drift from what the engine
/// writes; the engine's own capture of them is <see cref="ProviderFailureThroughTheEngineFlowTests"/>.
///
/// <para>Pins: the per-role matrix (an Owner, an Admin, a Member, a Viewer, and the credential's own person — who may reconnect their own
/// sign-in or replace their own pasted token on membership alone, the one rule both endpoints apply, agreed with the real commands), a retired credential, a failure traced through
/// the run's scope, an agent run's own account (only the LAST one, and only when the node that ended the run is the one that spawned it), a
/// run whose handled provider failure is not why it ended, a push the remote refused on a run that otherwise finished, a run of another
/// team, a redirect inside one origin (no address to edit), a base URL carrying userinfo (masked), and the refused-delivery remedy a
/// repository's trigger carries — told apart by whether the credential or the provider failed the lookup.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ProviderRemedyFlowTests
{
    private readonly PostgresFixture _fixture;

    public ProviderRemedyFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Who can press what ────────────────────────────────────────────────────

    [Theory]
    // viewer role of the asker, may reconnect, may re-link, may run it again
    [InlineData(TeamRole.Owner, true, true, false)]
    [InlineData(TeamRole.Admin, true, true, false)]
    [InlineData(TeamRole.Member, false, false, false)]
    [InlineData(TeamRole.Viewer, false, false, false)]
    public async Task The_same_failure_reads_differently_to_each_role_and_the_ones_who_cannot_are_told_who_can(TeamRole role, bool reconnect, bool relink, bool rerun)
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var asker = await AddMemberAsync(world.TeamId, role, $"Asker {role}");
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        var remedy = (await RemedyAsync(asker, world.TeamId, runId))!;
        var enabled = remedy.Actions.ToDictionary(a => a.Kind, a => a.Enabled);

        enabled[RemedyActionKind.Reconnect].ShouldBe(reconnect);
        enabled[RemedyActionKind.Relink].ShouldBe(relink);
        enabled[RemedyActionKind.OpenProviders].ShouldBeTrue("looking at the connections is every member's");
        enabled[RemedyActionKind.Rerun].ShouldBe(rerun, "nothing is fixed yet, so nobody may run it again");

        if (reconnect)
        {
            remedy.AskPeople.ShouldBeEmpty("every fix is the caller's own, so there is no one to ask");
            return;
        }

        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Reconnect).DisabledReason.ShouldBe($"Only {world.CredentialOwnerName}, or a team Admin, can do this.", "someone else's sign-in is the credentials.manage bar, the same as someone else's pasted token");
        remedy.AskPeople.Select(p => p.Role).ShouldBe(new[] { "Owner of the credential", "Owner", "Admin" }, "the person whose sign-in it is can reconnect it, then the team's Owners and Admins");
        remedy.AskPeople.ShouldAllBe(p => p.UserId != asker, "nobody is told to ask themselves");
    }

    [Theory]
    // role of the asker, may replace the token
    [InlineData(TeamRole.Owner, true)]
    [InlineData(TeamRole.Admin, true)]
    [InlineData(TeamRole.Member, false)]
    [InlineData(TeamRole.Viewer, false)]
    public async Task A_pasted_token_that_is_not_the_askers_is_theirs_to_replace_only_as_an_admin_and_the_credentials_person_is_named(TeamRole role, bool replace)
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member, authType: AuthType.Pat);
        var asker = await AddMemberAsync(world.TeamId, role, $"Asker {role}");
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        var remedy = (await RemedyAsync(asker, world.TeamId, runId))!;
        var action = remedy.Actions.Single(a => a.Kind == RemedyActionKind.ReplaceToken);

        action.Enabled.ShouldBe(replace);
        remedy.Actions.ShouldNotContain(a => a.Kind == RemedyActionKind.Reconnect, "a pasted token is replaced, not signed in again");

        if (replace) return;

        action.DisabledReason.ShouldBe($"Only {world.CredentialOwnerName}, or a team Admin, can do this.");
        remedy.AskPeople.Select(p => p.Role).ShouldBe(new[] { "Owner of the credential", "Owner", "Admin" }, "the person whose token it is, then the team's Owners and Admins");
        remedy.AskPeople[0].Name.ShouldBe(world.CredentialOwnerName);
    }

    [Fact]
    public async Task The_credentials_own_person_can_replace_their_pasted_token_as_a_plain_member_but_cannot_re_link_the_repository()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member, authType: AuthType.Pat);
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        var remedy = (await RemedyAsync(world.CredentialOwnerId, world.TeamId, runId))!;

        remedy.Actions.Single(a => a.Kind == RemedyActionKind.ReplaceToken).Enabled.ShouldBeTrue("a person replaces their own token on membership alone");
        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Relink).Enabled.ShouldBeFalse();
        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Relink).DisabledReason.ShouldBe("Needs the Admin or Owner role.");
        remedy.AskPeople.Select(p => p.Role).ShouldBe(new[] { "Owner", "Admin" });
    }

    [Fact]
    public async Task The_credentials_own_person_is_offered_the_reconnect_of_their_oauth_sign_in_as_a_plain_member_and_the_endpoint_accepts_it()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await ConfigureOAuthAppAsync(world.InstanceId);
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        var remedy = (await RemedyAsync(world.CredentialOwnerId, world.TeamId, runId))!;
        var reconnect = remedy.Actions.Single(a => a.Kind == RemedyActionKind.Reconnect);

        reconnect.Enabled.ShouldBeTrue("a person re-authorizes their own credential on membership alone");
        reconnect.DisabledReason.ShouldBeNull();
        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Relink).Enabled.ShouldBeFalse("re-linking a repository is still the Admin's");

        // The probe the button's state stands for: the same asker sending the same command is accepted by the same rule.
        using var scope = _fixture.BeginScopeAs(world.CredentialOwnerId, world.TeamId);
        var started = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Credentials.InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceId, DisplayName = "GitLab · nacho", ReplaceCredentialId = world.CredentialId });

        started.State.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// Every way a person can stand toward a credential: the asker's role, whether the credential is theirs (then the person's role is the
    /// asker's own) or someone else's with a role of its own, and whether that person has since left the team without the credential
    /// being retired yet. Each for a sign-in and a pasted token.
    /// </summary>
    public static TheoryData<TeamRole, bool, TeamRole, bool, AuthType> RepairMatrix()
    {
        var roles = new[] { TeamRole.Owner, TeamRole.Admin, TeamRole.Member, TeamRole.Viewer };
        var data = new TheoryData<TeamRole, bool, TeamRole, bool, AuthType>();

        foreach (var authType in new[] { AuthType.OAuth, AuthType.Pat })
            foreach (var asker in roles)
            {
                data.Add(asker, true, asker, false, authType);

                foreach (var person in roles) data.Add(asker, false, person, false, authType);

                data.Add(asker, false, TeamRole.Member, true, authType);
            }

        return data;
    }

    [Theory]
    [MemberData(nameof(RepairMatrix))]
    public async Task A_repair_button_is_enabled_exactly_when_the_endpoint_behind_it_accepts_that_asker(TeamRole askerRole, bool owns, TeamRole personRole, bool personLeft, AuthType authType)
    {
        // The agreement the remedy exists to keep, measured against the real commands: what the remedy decides for a person is what the
        // pipeline and the service answer that same person, and the Providers list says the same. Accepted means the command RETURNED — any
        // exception, of any kind, is a refusal — so a guard the button does not know about cannot read as "accepted".
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedCredentialWorldAsync(owner: owns ? askerRole : personRole, authType, gitlab);
        var asker = owns ? world.CredentialOwnerId : await AddMemberAsync(world.TeamId, askerRole, $"Asker {askerRole}");
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        if (personLeft) await RemoveMembershipAsync(world.TeamId, world.CredentialOwnerId);

        var remedy = (await RemedyAsync(asker, world.TeamId, runId))!;
        var kind = authType == AuthType.OAuth ? RemedyActionKind.Reconnect : RemedyActionKind.ReplaceToken;
        var offered = remedy.Actions.Single(a => a.Kind == kind).Enabled;

        var listed = (await ListedAsync(asker, world.TeamId)).Single(c => c.Id == world.CredentialId).CallerMayRepair;

        var accepted = await AcceptedByTheEndpointAsync(asker, world, authType);

        var who = $"a {askerRole} who {(owns ? "owns" : "does not own")} a credential whose person {(personLeft ? "has left" : $"is a {personRole}")}";
        offered.ShouldBe(accepted, $"the {kind} button for {who} must be enabled exactly when its endpoint accepts them");
        listed.ShouldBe(accepted, $"the Providers list, which the repository rows read, must say the same of {who}");
    }

    private async Task<IReadOnlyList<CredentialSummary>> ListedAsync(Guid asker, Guid teamId)
    {
        using var scope = _fixture.BeginScopeAs(asker, teamId);

        return await scope.Resolve<ICredentialService>().ListAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task An_admin_looking_at_an_owners_sign_in_is_told_it_is_the_owners_alone_and_is_not_sent_to_ask_a_peer()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Owner);
        var admin = await AddMemberAsync(world.TeamId, TeamRole.Admin, "Asker Admin");
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        var remedy = (await RemedyAsync(admin, world.TeamId, runId))!;
        var reconnect = remedy.Actions.Single(a => a.Kind == RemedyActionKind.Reconnect);

        reconnect.Enabled.ShouldBeFalse();
        reconnect.DisabledReason.ShouldBe($"Only {world.CredentialOwnerName} can reconnect their own sign-in.");
        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Relink).Enabled.ShouldBeTrue("an Admin can still move the repository off it");
        remedy.AskPeople.Select(p => p.UserId).ShouldBe(new[] { world.CredentialOwnerId }, "the Admin who is asking and the Admin beside them cannot sign in as an Owner, so only the person is named");
        remedy.AskPeople.Single().Role.ShouldBe("Owner of the credential");
    }

    [Fact]
    public async Task A_peer_admins_sign_in_is_the_persons_or_an_owners_and_only_they_are_named()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Admin);
        var admin = await AddMemberAsync(world.TeamId, TeamRole.Admin, "Asker Admin");
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        var remedy = (await RemedyAsync(admin, world.TeamId, runId))!;

        remedy.Actions.Single(a => a.Kind == RemedyActionKind.Reconnect).DisabledReason.ShouldBe($"Only {world.CredentialOwnerName}, or a team Owner, can reconnect this sign-in.");
        remedy.AskPeople.Select(p => (p.UserId, p.Role)).ShouldBe(new[] { (world.CredentialOwnerId, "Owner of the credential"), (world.OwnerId, "Owner") }, "Ada, the other Admin, could not sign in as a peer any more than the asker: only the person and the team's Owner can");
    }

    [Theory]
    [InlineData(AuthType.OAuth)]
    [InlineData(AuthType.Pat)]
    public async Task A_credential_whose_person_left_is_nobodys_to_repair_and_names_nobody_for_it(AuthType authType)
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member, authType);
        var member = await AddMemberAsync(world.TeamId, TeamRole.Member, "Asker Member");
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));
        await RemoveMembershipAsync(world.TeamId, world.CredentialOwnerId);

        var remedy = (await RemedyAsync(member, world.TeamId, runId))!;
        var repair = remedy.Actions.Single(a => a.Kind is RemedyActionKind.Reconnect or RemedyActionKind.ReplaceToken);

        repair.DisabledReason.ShouldBe($"{world.CredentialOwnerName} has left this team, so this cannot be repaired.");
        remedy.Actions.Select(a => a.Kind).First().ShouldBe(RemedyActionKind.Relink, "moving off it is the way out, as for a retired credential");
        remedy.AskPeople.Select(p => p.UserId).ShouldNotContain(world.CredentialOwnerId, "someone who left cannot be asked");
        remedy.AskPeople.Select(p => p.Role).ShouldBe(new[] { "Owner", "Admin" }, "the Admins who can re-link, which a Member cannot");
    }

    [Fact]
    public async Task A_credential_whose_person_left_but_is_not_retired_yet_reads_as_owner_left_beside_the_failure()
    {
        // A 401 on a credential still marked Active: nothing records that it is refused, so what the card says about it is what the list would.
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await SetStatusAsync(world.CredentialId, CredentialStatus.Active);
        var runId = await SeedFailedRunAsync(world, new ProviderApiException(ProviderKind.GitLab, 401, "ListPullRequestsAsync", "401", new Exception("x")));
        await RemoveMembershipAsync(world.TeamId, world.CredentialOwnerId);

        var remedy = (await RemedyAsync(world.OwnerId, world.TeamId, runId))!;

        remedy.Credential!.Health.ShouldBe(CredentialHealth.OwnerLeft);
        (await ListedAsync(world.OwnerId, world.TeamId)).Single(c => c.Id == world.CredentialId).Health.ShouldBe(CredentialHealth.OwnerLeft, "the list and the card read the same row alike");
    }

    [Fact]
    public async Task A_team_service_sign_in_is_refused_to_an_admin_by_the_endpoint_and_the_button_says_so_without_blaming_their_role()
    {
        // Not something the product mints — a connect always yields a Personal credential — but a row the server refuses to reconnect all the same.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member, AuthType.OAuth, gitlab);
        await MakeTeamServiceAsync(world.CredentialId);
        var admin = await AddMemberAsync(world.TeamId, TeamRole.Admin, "Asker Admin");
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"));

        var reconnect = (await RemedyAsync(admin, world.TeamId, runId))!.Actions.Single(a => a.Kind == RemedyActionKind.Reconnect);

        reconnect.Enabled.ShouldBeFalse();
        reconnect.DisabledReason.ShouldBe("A team's own credential has no one's sign-in to reconnect; replace its token instead.");
        (await AcceptedByTheEndpointAsync(admin, world, AuthType.OAuth)).ShouldBeFalse();
        (await ListedAsync(admin, world.TeamId)).Single(c => c.Id == world.CredentialId).CallerMayRepair.ShouldBe(false);
    }

    /// <summary>The command the button runs, as the asker sends it: true when it returns, false for any refusal at all.</summary>
    private async Task<bool> AcceptedByTheEndpointAsync(Guid asker, World world, AuthType authType)
    {
        using var scope = _fixture.BeginScopeAs(asker, world.TeamId);
        var mediator = scope.Resolve<MediatR.IMediator>();

        var thrown = await Record.ExceptionAsync(() => authType == AuthType.OAuth
            ? mediator.Send(new CodeSpace.Messages.Commands.Credentials.InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceId, DisplayName = "GitLab · nacho", ReplaceCredentialId = world.CredentialId })
            : mediator.Send(new CodeSpace.Messages.Commands.Credentials.ReplaceCredentialTokenCommand { CredentialId = world.CredentialId, Token = FreshToken }));

        return thrown is null;
    }

    [Fact]
    public async Task A_retired_credential_names_that_it_was_retired_and_offers_only_a_re_link()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await RetireAsync(world.CredentialId);
        var runId = await SeedFailedRunAsync(world, new CredentialDisconnectedException(world.CredentialId, "GitLab · nacho"));

        var remedy = (await RemedyAsync(world.OwnerId, world.TeamId, runId))!;

        remedy.Credential!.Retired.ShouldBeTrue();
        remedy.Title.ShouldBe("The credential this run used was retired");
        remedy.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Relink, RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
        remedy.Resolved.ShouldBeFalse();
    }

    // ─── Where the failure is read from ────────────────────────────────────────

    [Fact]
    public async Task A_redirect_inside_one_origin_offers_no_address_edit_because_there_is_no_address_to_edit_to()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var address = new Uri(await BaseUrlOfAsync(world.InstanceId));
        var runId = await SeedFailedRunAsync(world, new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, address, new Uri(address, "/elsewhere")));

        var remedy = (await RemedyAsync(world.OwnerId, world.TeamId, runId))!;

        remedy.Code.ShouldBe(FailureCodes.ProviderBaseUrlRedirects);
        remedy.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders, RemedyActionKind.Rerun });
        remedy.Cause.ShouldContain("remove the redirect on the host");
        remedy.Cause.ShouldNotContain("Change the provider's address", Case.Insensitive);
    }

    [Fact]
    public async Task The_instance_base_url_a_remedy_carries_is_masked_like_the_provider_list_masks_it()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await SetBaseUrlAsync(world.InstanceId, "http://deploy:glpat-s3cret@127.0.0.1:1/gitlab");
        var runId = await SeedFailedRunAsync(world, new CredentialDisconnectedException(world.CredentialId, "GitLab · nacho"));

        var viewer = await AddMemberAsync(world.TeamId, TeamRole.Viewer, "Vera Viewer");
        var remedy = (await RemedyAsync(viewer, world.TeamId, runId))!;

        remedy.ProviderInstance!.BaseUrl.ShouldBe("http://***:***@127.0.0.1:1/gitlab", "a Viewer reads the run, and a base URL may carry userinfo the list never shows");
        System.Text.Json.JsonSerializer.Serialize(remedy).ShouldNotContain("s3cret");
    }

    [Fact]
    public async Task A_provider_401_with_no_repository_recorded_is_traced_through_the_repositories_the_run_was_launched_against()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var runId = await SeedFailedRunAsync(world, new ProviderApiException(ProviderKind.GitLab, 401, "ListPullRequestsAsync", "401", new Exception("x")), repositoryId: null, scopeRepositories: new[] { world.RepositoryId });

        var remedy = (await RemedyAsync(world.OwnerId, world.TeamId, runId))!;

        remedy.Code.ShouldBe(FailureCodes.ProviderUnauthorized);
        remedy.Credential!.Id.ShouldBe(world.CredentialId);
        remedy.Repositories.ShouldHaveSingleItem().Id.ShouldBe(world.RepositoryId);
    }

    [Fact]
    public async Task An_agent_runs_own_failure_speaks_for_a_failed_run_whose_ledger_names_none_and_only_the_last_agent_run_counts()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var runId = await SeedRunAsync(world, WorkflowRunStatus.Failure);

        await SeedAgentRunAsync(world, runId, AgentRunStatus.Failed, createdMinutesAgo: 10, result: ResultOf(Failure: ProviderFailureCapture.From(new CredentialDisconnectedException(world.CredentialId, "n"), world.RepositoryId)));
        (await RemedyAsync(world.OwnerId, world.TeamId, runId))!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "the clone could not authenticate");

        await SeedAgentRunAsync(world, runId, AgentRunStatus.Failed, createdMinutesAgo: 1, result: ResultOf(Failure: null));
        (await RemedyAsync(world.OwnerId, world.TeamId, runId)).ShouldBeNull("a later attempt that failed for another reason is why the run ended; the earlier credential is history");
    }

    [Fact]
    public async Task An_agent_runs_failure_speaks_for_the_run_only_when_the_node_that_ended_the_run_is_the_one_that_spawned_it()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var disconnected = ResultOf(Failure: ProviderFailureCapture.From(new CredentialDisconnectedException(world.CredentialId, "n"), world.RepositoryId));

        var endedOnAnotherNode = await SeedRunAsync(world, WorkflowRunStatus.Failure, error: "Node 'later' failed.");
        await SeedAgentRunAsync(world, endedOnAnotherNode, AgentRunStatus.Failed, createdMinutesAgo: 5, result: disconnected);
        await RecordNodeFailureAsync(endedOnAnotherNode, "later");

        var endedOnTheAgent = await SeedRunAsync(world, WorkflowRunStatus.Failure, error: "Node 'agent' failed.");
        await SeedAgentRunAsync(world, endedOnTheAgent, AgentRunStatus.Failed, createdMinutesAgo: 5, result: disconnected);
        await RecordNodeFailureAsync(endedOnTheAgent, "agent");

        (await RemedyAsync(world.OwnerId, world.TeamId, endedOnAnotherNode)).ShouldBeNull("the agent run's connection failure is history: the run ended on 'later', which recorded none");
        (await RemedyAsync(world.OwnerId, world.TeamId, endedOnTheAgent))!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "the agent node ended the run and its own record names no failure, so its agent run's account is the reason");
    }

    [Fact]
    public async Task A_handled_provider_failure_does_not_speak_for_a_run_the_reconciler_later_abandoned_or_one_that_failed_on_another_node()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var thrown = new CredentialDisconnectedException(world.CredentialId, "GitLab · nacho");

        var abandoned = await SeedFailedRunAsync(world, thrown);
        await SetRunErrorAsync(abandoned, "Run marked abandoned by reconciler — worker crashed or hung past the abandoned-run threshold with no ledger progress.");

        var failedLater = await SeedFailedRunAsync(world, thrown);
        await RecordNodeFailureAsync(failedLater, "flaky");
        await SetRunErrorAsync(failedLater, "Node 'flaky' failed.");

        var failedOnIt = await SeedFailedRunAsync(world, thrown);

        (await RemedyAsync(world.OwnerId, world.TeamId, abandoned)).ShouldBeNull("the run's error names no node, so the failure on 'list' is not what ended it");
        (await RemedyAsync(world.OwnerId, world.TeamId, failedLater)).ShouldBeNull("the newest node to fail recorded no connection failure");
        (await RemedyAsync(world.OwnerId, world.TeamId, failedOnIt))!.Code.ShouldBe(FailureCodes.CredentialDisconnected, "control: the node the run's error names recorded it");
    }

    [Fact]
    public async Task A_push_the_remote_refused_on_a_run_that_otherwise_finished_has_a_remedy_naming_the_repository()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var runId = await SeedRunAsync(world, WorkflowRunStatus.Success);

        await SeedAgentRunAsync(world, runId, AgentRunStatus.Succeeded, createdMinutesAgo: 1, result: ResultOf(PublishFailureCode: FailureCodes.GitCredentialRejected, RepositoryResultId: world.RepositoryId));

        var remedy = (await RemedyAsync(world.OwnerId, world.TeamId, runId))!;

        remedy.Code.ShouldBe(FailureCodes.GitCredentialRejected);
        remedy.Title.ShouldBe("GitLab refused the push");
        remedy.Repositories.ShouldHaveSingleItem().Id.ShouldBe(world.RepositoryId);
        remedy.Credential!.Id.ShouldBe(world.CredentialId);
    }

    [Fact]
    public async Task A_finished_run_whose_agents_published_cleanly_has_no_remedy()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var runId = await SeedRunAsync(world, WorkflowRunStatus.Success);

        await SeedAgentRunAsync(world, runId, AgentRunStatus.Succeeded, createdMinutesAgo: 1, result: ResultOf());

        (await RemedyAsync(world.OwnerId, world.TeamId, runId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_run_of_another_team_reads_as_nothing()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var other = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var runId = await SeedFailedRunAsync(world, new CredentialDisconnectedException(world.CredentialId, "n"));

        (await RemedyAsync(other.OwnerId, other.TeamId, runId)).ShouldBeNull("the run is not the asking team's, so it is indistinguishable from a run that does not exist");
    }

    [Fact]
    public async Task Both_run_detail_reads_carry_the_remedy_for_the_asker_and_a_run_that_did_not_fail_carries_none()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var failed = await SeedFailedRunAsync(world, new CredentialDisconnectedException(world.CredentialId, "GitLab · nacho"));
        var succeeded = await SeedRunAsync(world, WorkflowRunStatus.Success);
        var viewer = await AddMemberAsync(world.TeamId, TeamRole.Viewer, "Vera Viewer");

        using var scope = _fixture.BeginScopeAs(viewer, world.TeamId);
        var mediator = scope.Resolve<MediatR.IMediator>();

        var byId = await mediator.Send(new CodeSpace.Messages.Queries.Workflows.GetWorkflowRunQuery { RunId = failed });
        var byRef = await mediator.Send(new CodeSpace.Messages.Queries.Workflows.GetWorkflowRunByRefQuery { IdOrNumber = failed.ToString() });

        foreach (var detail in new[] { byId, byRef })
        {
            detail!.Remedy.ShouldNotBeNull("the run failed on a disconnected credential");
            detail.Remedy.Code.ShouldBe(FailureCodes.CredentialDisconnected);
            detail.Remedy.Actions.Where(a => a.Enabled).Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.OpenProviders }, "a Viewer's remedy, not an Owner's");
            detail.Error.ShouldBe("Node 'list' failed.", "the sentence is untouched");
        }

        (await mediator.Send(new CodeSpace.Messages.Queries.Workflows.GetWorkflowRunQuery { RunId = succeeded }))!.Remedy.ShouldBeNull();
        (await mediator.Send(new CodeSpace.Messages.Queries.Workflows.GetWorkflowRunRemedyQuery { RunId = failed }))!.Code.ShouldBe(FailureCodes.CredentialDisconnected);
    }

    // ─── The refused deliveries a repository's trigger carries ─────────────────

    [Fact]
    public async Task Deliveries_refused_for_the_credential_carry_a_remedy_with_no_run_to_run_again()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.CredentialRejected, "GitLab answered HTTP 401 to the repository's credential 'GitLab · nacho'", minutesAgo: 3);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.SignatureInvalid, "HMAC mismatch", minutesAgo: 1);

        var remedy = (await RefusedRemedyAsync(world.OwnerId, world, world.RepositoryId))!;

        remedy.Code.ShouldBe(WorkflowRunRequestRejectionReasons.CredentialRejected, "the newest refusal that blames the credential — a signature failure blames nothing here");
        remedy.Title.ShouldBe("GitLab refused the credential this trigger uses");
        remedy.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Reconnect, RemedyActionKind.Relink, RemedyActionKind.OpenProviders }, "a refused delivery started no run, so there is nothing to run again");
        remedy.Credential!.Id.ShouldBe(world.CredentialId);
    }

    [Fact]
    public async Task A_standing_the_provider_would_not_answer_says_what_the_credential_needs_and_is_never_resolved_by_a_live_row()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, "GitLab refused the lookup (HTTP 403)", minutesAgo: 3);

        var remedy = (await RefusedRemedyAsync(world.OwnerId, world, world.RepositoryId))!;

        remedy.Code.ShouldBe(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown);
        remedy.Cause.ShouldContain("read_api scope");
        remedy.Resolved.ShouldBeFalse("a 403 on a credential that looks fine is only believed fixed once something validated it since");
    }

    [Theory]
    // what the refusal kept, what the card must offer
    [InlineData(StandingLookupCauses.AccessRefused, "403", true, "lacks the read_api scope or a role on the project")]
    [InlineData(StandingLookupCauses.NoAnswer, "503", false, "is not a verdict on")]
    [InlineData(StandingLookupCauses.NoAnswer, null, false, "did not answer in time")]
    public async Task A_standing_the_provider_could_not_give_names_what_failed_and_sends_the_credentials_person_to_repair_it_only_when_it_is_the_credential(string lookup, string? status, bool repairsTheCredential, string says)
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, "GitLab answered", minutesAgo: 3, metadata: LookupMetadata(lookup, status));

        var remedy = (await RefusedRemedyAsync(world.OwnerId, world, world.RepositoryId))!;
        var kinds = remedy.Actions.Select(a => a.Kind).ToList();

        remedy.Cause.ShouldContain(says);
        remedy.Cause.ShouldContain("Anyone is not the fix");
        kinds.Contains(RemedyActionKind.Reconnect).ShouldBe(repairsTheCredential, "a lookup that got no answer is not a reason to repair a credential nobody refused");
        kinds.Contains(RemedyActionKind.Relink).ShouldBe(repairsTheCredential);
        kinds.ShouldContain(RemedyActionKind.OpenProviders);
        if (status is not null) remedy.Cause.ShouldContain($"(HTTP {status})");
    }

    [Fact]
    public async Task A_repository_that_had_no_credential_to_ask_with_is_offered_a_re_link_and_nothing_to_reconnect()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await UnbindAsync(world.RepositoryId);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, "the repository has no credential bound to ask the provider with", minutesAgo: 3, metadata: LookupMetadata(StandingLookupCauses.NoCredential, null));

        var remedy = (await RefusedRemedyAsync(world.OwnerId, world, world.RepositoryId))!;

        remedy.Title.ShouldBe("There is no credential to ask GitLab who wrote the pull request with");
        remedy.Actions.Select(a => a.Kind).ShouldBe(new[] { RemedyActionKind.Relink, RemedyActionKind.OpenProviders });
    }

    [Fact]
    public async Task A_standing_refusal_written_before_the_cause_was_kept_reads_as_either_and_keeps_the_repair()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, "GitLab refused the lookup (HTTP 403)", minutesAgo: 3);

        var remedy = (await RefusedRemedyAsync(world.OwnerId, world, world.RepositoryId))!;

        remedy.Cause.ShouldContain("can also be a timeout or an error");
        remedy.Actions.Select(a => a.Kind).ShouldContain(RemedyActionKind.Reconnect, "what it always offered: nothing is known that says the credential is fine");
    }

    [Fact]
    public async Task Refusals_that_blame_nothing_about_the_credential_carry_no_remedy()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.SignatureInvalid, "HMAC mismatch", minutesAgo: 1);
        await SeedRefusalAsync(world, WorkflowRunRequestRejectionReasons.AuthorNotMember, "not a member", minutesAgo: 2);

        (await RefusedRemedyAsync(world.OwnerId, world, world.RepositoryId)).ShouldBeNull();
    }

    // ─── The credential list ───────────────────────────────────────────────────

    [Fact]
    public async Task The_credential_list_returns_every_state_a_person_may_need_to_repair_with_how_many_repositories_each_holds()
    {
        var world = await SeedCredentialWorldAsync(owner: TeamRole.Member);
        var revoked = await AddCredentialAsync(world, CredentialStatus.Revoked, "GitLab · disconnected");
        var errored = await AddCredentialAsync(world, CredentialStatus.Error, "GitLab · refused");
        var retired = await AddCredentialAsync(world, CredentialStatus.Revoked, "GitLab · retired", deleted: true);
        await AddRepositoryAsync(world, revoked);
        await AddRepositoryAsync(world, revoked);
        await AddRepositoryAsync(world, errored);

        using var scope = _fixture.BeginScopeAs(world.OwnerId, world.TeamId);
        var list = await scope.Resolve<ICredentialService>().ListAsync(null, CancellationToken.None);

        list.Single(c => c.Id == world.CredentialId).BoundRepositoryCount.ShouldBe(1);
        list.Single(c => c.Id == revoked).BoundRepositoryCount.ShouldBe(2);
        list.Single(c => c.Id == revoked).Health.ShouldBe(CredentialHealth.Revoked, "a disconnected credential is still listed, so a person can see what is stranded");
        list.Single(c => c.Id == errored).Health.ShouldBe(CredentialHealth.Rejected);
        list.Single(c => c.Id == errored).BoundRepositoryCount.ShouldBe(1);
        list.ShouldNotContain(c => c.Id == retired, "a retired credential is gone from the list; what leans on it is reached through its repositories");
    }

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid OwnerId, Guid CredentialOwnerId, string CredentialOwnerName, Guid InstanceId, Guid CredentialId, Guid RepositoryId);

    /// <summary>The token a replace is sent, which the stub knows as the credential's own account's.</summary>
    private const string FreshToken = "glpat-fresh";

    /// <summary>
    /// A team with an Owner, an Admin, and a person (<paramref name="owner"/>'s role) who owns the one credential, which a repository runs
    /// through. Given a <paramref name="gitlab"/>, the provider connection is that stub's — with its OAuth app, the person's account on it and
    /// <see cref="FreshToken"/> as a token of theirs — so a connect or a token replace the remedy offers can be sent for real and succeed.
    /// </summary>
    private async Task<World> SeedCredentialWorldAsync(TeamRole owner, AuthType authType = AuthType.OAuth, GitLabDoorkeeperStub? gitlab = null)
    {
        var team = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var admin = await AddMemberAsync(team.TeamId, TeamRole.Admin, "Ada Admin");
        var nacho = await AddMemberAsync(team.TeamId, owner, "Nacho");
        var nachoName = await NameOfAsync(nacho);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var address = gitlab?.BaseUrl ?? $"http://127.0.0.1:1/{suffix}";
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.TeamId, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = address, ApiUrl = address };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team.TeamId, ProviderInstanceId = instance.Id, OwnerUserId = nacho, Ownership = CredentialOwnership.Personal, AuthType = authType, DisplayName = "GitLab · nacho", EncryptedPayload = "x", Status = CredentialStatus.Error, LastError = "needs reconnect: invalid_grant" };
        var repository = NewRepository(team.TeamId, instance.Id, credential.Id, "acme/api");

        if (gitlab is not null)
        {
            instance.OauthClientId = GitLabDoorkeeperStub.DefaultClientId;
            instance.OauthClientSecretEnc = encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret);
            credential.EncryptedPayload = encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(authType == AuthType.OAuth ? new OAuthPayload { AccessToken = "at-0", RefreshToken = "rt-0", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } : new PatPayload { Token = "glpat-expired" }));
            gitlab.SeedPersonalAccessToken("nacho", FreshToken);
            db.UserProviderIdentity.Add(new UserProviderIdentity { Id = Guid.NewGuid(), UserId = nacho, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ProviderUserId = gitlab.UserIdOf("nacho").ToString(), ProviderUsername = "nacho" });
        }

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        await db.SaveChangesAsync();

        _ = admin;

        return new World(team.TeamId, team.UserId, nacho, nachoName, instance.Id, credential.Id, repository.Id);
    }

    private async Task RemoveMembershipAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == teamId && m.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>Gives the provider connection the OAuth app a connect needs, so a start that is let in by authorization is not then refused for having none.</summary>
    private async Task ConfigureOAuthAppAsync(Guid instanceId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var instance = await db.ProviderInstance.SingleAsync(i => i.Id == instanceId);

        instance.OauthClientId = "remedy-app";
        instance.OauthClientSecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("remedy-secret");
        await db.SaveChangesAsync();
    }

    private async Task<string> NameOfAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().User.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).SingleAsync();
    }

    private static Repository NewRepository(Guid teamId, Guid instanceId, Guid? credentialId, string path) => new()
    {
        Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = path.Split('/')[0], Name = path.Split('/')[1], FullPath = path,
        DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"http://127.0.0.1:1/{path}", Status = RepositoryStatus.Error,
    };

    private async Task<Guid> AddMemberAsync(Guid teamId, TeamRole role, string name)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"remedy-{userId:N}@test.local", Name = $"{name} {userId.ToString("N")[..4]}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();

        return userId;
    }

    private async Task<string> BaseUrlOfAsync(Guid instanceId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ProviderInstance.AsNoTracking().Where(i => i.Id == instanceId).Select(i => i.BaseUrl).SingleAsync();
    }

    private async Task SetBaseUrlAsync(Guid instanceId, string baseUrl)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE provider_instance SET base_url = {baseUrl} WHERE id = {instanceId}");
    }

    private async Task SetStatusAsync(Guid credentialId, CredentialStatus status)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = {status.ToString()}, last_error = NULL WHERE id = {credentialId}");
    }

    private async Task MakeTeamServiceAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET ownership = 'TeamService', owner_user_id = NULL WHERE id = {credentialId}");
    }

    private async Task RetireAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = 'Revoked', deleted_date = now() WHERE id = {credentialId}");
    }

    private async Task<Guid> AddCredentialAsync(World world, CredentialStatus status, string name, bool deleted = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = world.InstanceId, OwnerUserId = world.CredentialOwnerId, AuthType = AuthType.OAuth, DisplayName = name, EncryptedPayload = "x", Status = status, DeletedDate = deleted ? DateTimeOffset.UtcNow : null };

        db.Credential.Add(credential);
        await db.SaveChangesAsync();

        return credential.Id;
    }

    private async Task AddRepositoryAsync(World world, Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Repository.Add(NewRepository(world.TeamId, world.InstanceId, credentialId, $"acme/r{Guid.NewGuid().ToString("N")[..6]}"));
        await db.SaveChangesAsync();
    }

    /// <summary>A failed run whose node recorded the failure the real exception declares, written by the real ledger writer.</summary>
    private async Task<Guid> SeedFailedRunAsync(World world, Exception thrown, Guid? repositoryId = null, Guid[]? scopeRepositories = null)
    {
        var runId = await SeedRunAsync(world, WorkflowRunStatus.Failure, scopeRepositories);
        var failure = ProviderFailureCapture.From(thrown, repositoryId ?? (scopeRepositories is null ? world.RepositoryId : null));

        using var scope = _fixture.BeginScope();

        await scope.Resolve<IRunRecordLogger>().NodeFailedAsync(runId, "list", string.Empty, thrown.Message, TimeSpan.FromMilliseconds(5), failure, CancellationToken.None);

        return runId;
    }

    private async Task<Guid> SeedRunAsync(World world, WorkflowRunStatus status, Guid[]? scopeRepositories = null, string? error = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = world.TeamId, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, TeamId = world.TeamId, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot, Status = status, Error = error ?? (status == WorkflowRunStatus.Failure ? "Node 'list' failed." : null),
            ScopeRepositoryIds = scopeRepositories?.ToList() ?? [], ScopeProjectIds = [], DefinitionSnapshotJson = "{\"nodes\":[],\"edges\":[]}", DefinitionSnapshotHash = "sha256:test", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();

        return runId;
    }

    /// <summary>A node that failed with no provider failure — written by the real ledger writer, after any record already there.</summary>
    private async Task RecordNodeFailureAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<IRunRecordLogger>().NodeFailedAsync(runId, nodeId, string.Empty, "it failed", TimeSpan.FromMilliseconds(5), CancellationToken.None);
    }

    private async Task SetRunErrorAsync(Guid runId, string error)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET error = {error} WHERE id = {runId}");
    }

    private static string ResultOf(PersistedFailure? Failure = null, string? PublishFailureCode = null, Guid? RepositoryResultId = null)
    {
        var result = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed", Failure = Failure, PublishFailureCode = RepositoryResultId is null ? PublishFailureCode : null,
            RepositoryResults = RepositoryResultId is { } id ? new[] { new RepositoryRunResult { Alias = "api", RepositoryId = id, PublishFailureCode = PublishFailureCode } } : Array.Empty<RepositoryRunResult>(),
        };

        return System.Text.Json.JsonSerializer.Serialize(result, CodeSpace.Core.Services.Agents.AgentJson.Options);
    }

    private async Task SeedAgentRunAsync(World world, Guid runId, AgentRunStatus status, int createdMinutesAgo, string result)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var at = DateTimeOffset.UtcNow.AddMinutes(-createdMinutesAgo);

        db.AgentRun.Add(new AgentRun { Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = runId, NodeId = "agent", Harness = "claude-cli", Status = status, ResultJson = result, CompletedAt = at, CreatedDate = at, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
    }

    private static string LookupMetadata(string lookup, string? status) => CodeSpace.Core.Services.Workflows.Failures.PersistedFailureJson.AsPayload(new PersistedFailure
    {
        Code = WorkflowRunRequestRejectionReasons.AuthorStandingUnknown,
        Details = status is null ? new Dictionary<string, string> { [StandingLookupCauses.DetailKey] = lookup } : new Dictionary<string, string> { [StandingLookupCauses.DetailKey] = lookup, ["providerStatus"] = status }
    });

    private async Task UnbindAsync(Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE repository SET credential_id = NULL WHERE id = {repositoryId}");
    }

    private async Task SeedRefusalAsync(World world, string reason, string detail, int minutesAgo, string? metadata = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, RepositoryId = world.RepositoryId, SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}gitlab", ExternalEventId = Guid.NewGuid().ToString("N"), ActorType = WorkflowRunActorTypes.Webhook,
            Status = WorkflowRunRequestStatus.Rejected, Error = $"{reason}: {detail}", ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), RequestMetadataJson = metadata ?? "{}",
        });
        await db.SaveChangesAsync();
    }

    private async Task<ProviderRemedy?> RemedyAsync(Guid askerId, Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScopeAs(askerId, teamId);

        return await scope.Resolve<IProviderRemedyService>().GetForRunAsync(runId, teamId, CancellationToken.None);
    }

    private async Task<ProviderRemedy?> RefusedRemedyAsync(Guid askerId, World world, Guid repositoryId)
    {
        using var scope = _fixture.BeginScopeAs(askerId, world.TeamId);
        var refused = await scope.Resolve<IRejectedDeliveryReader>().ListForRepositoryAsync(repositoryId, CancellationToken.None);

        return await scope.Resolve<IProviderRemedyService>().GetForRefusedDeliveriesAsync(repositoryId, refused.Deliveries, world.TeamId, CancellationToken.None);
    }
}
