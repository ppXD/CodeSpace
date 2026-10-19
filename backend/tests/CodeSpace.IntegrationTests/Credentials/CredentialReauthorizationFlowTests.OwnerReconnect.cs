using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Who may start a reconnect, and who may finish it: the person a credential belongs to re-authorizes it on membership alone, anyone
/// else needs <c>credentials.manage</c> — the bar <c>PUT /api/credentials/{id}/token</c> already applies, so putting a sign-in
/// right is no harder than putting a pasted token right. A connect that names no credential mints one, and keeps its bar.
///
/// <para>Through the real mediator pipeline and the real callback, against real Postgres. Every other guard of a re-authorization — the
/// same provider account, a retired credential, an owner without standing, the rank reach over the person acted on — is unchanged and
/// pinned in the tests around these; the ones here are the guards that moved with the owner's pass.</para>
/// </summary>
public partial class CredentialReauthorizationFlowTests
{
    // ─── The credential's own person ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public async Task A_credentials_own_person_reconnects_it_in_place_whatever_their_role(TeamRole role)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var (ownerId, account) = role == TeamRole.Member ? (world.MayaId, "maya") : (world.ViewerId, "viewer");
        var credentialId = await SeedOAuthCredentialAsync(world, ownerId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        var repositoryId = await SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error).ConfigureAwait(false);
        await SeedIdentityAsync(world, ownerId, credentialId, account).ConfigureAwait(false);

        var finished = await ReconnectAsync(world, ownerId, credentialId, $"authz:{account}").ConfigureAwait(false);

        finished.CredentialId.ShouldBe(credentialId, "the credential keeps its id: nothing bound through it moves");
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1, "no second row was minted");
        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0, "finishing spends the state");

        var row = await LoadCredentialAsync(credentialId).ConfigureAwait(false);
        row.Status.ShouldBe(CredentialStatus.Active);
        row.LastError.ShouldBeNull();
        row.LastModifiedBy.ShouldBe(ownerId, "attributed to the person who reconnected");
        (await StoredPairAsync(credentialId).ConfigureAwait(false)).RefreshToken.ShouldBe(gitlab.LiveRefreshTokenOf(account), "the row holds the pair GitLab issued for this sign-in");
        (await LoadRepositoryAsync(repositoryId).ConfigureAwait(false)).ShouldSatisfyAllConditions(r => r.CredentialId.ShouldBe(credentialId), r => r.Status.ShouldBe(RepositoryStatus.Active));
    }

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public async Task A_credentials_own_person_finishes_it_as_the_initiator_when_the_callback_was_handed_to_the_spa(TeamRole role)
    {
        // The cross-site door: the callback exchanged nothing, and the signed-in starter posts the code to /complete.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var (ownerId, account) = role == TeamRole.Member ? (world.MayaId, "maya") : (world.ViewerId, "viewer");
        var credentialId = await SeedOAuthCredentialAsync(world, ownerId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        await SeedIdentityAsync(world, ownerId, credentialId, account).ConfigureAwait(false);
        var init = await InitAsync(world, ownerId, credentialId).ConfigureAwait(false);

        using var scope = Scope(world, ownerId);
        var finished = await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthAsInitiatorCommand { State = init.State, Code = $"authz:{account}" }).ConfigureAwait(false);

        finished.CredentialId.ShouldBe(credentialId);
        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
        (await CountCredentialsAsync(world).ConfigureAwait(false)).ShouldBe(1);
    }

    // ─── Anyone else ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public async Task Someone_who_does_not_manage_credentials_cannot_start_a_reconnect_of_a_teammates_credential(TeamRole role)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var nachos = await SeedOAuthCredentialAsync(world, world.NachoId, "at-n", "rt-n").ConfigureAwait(false);
        var asker = role == TeamRole.Member ? world.MayaId : world.ViewerId;

        var refusal = await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, asker, nachos)).ConfigureAwait(false);

        refusal.ClientMessage.ShouldContain($"Your role on this team is {role}");
        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0, "a refused start leaves no pending connect behind");
    }

    [Fact]
    public async Task A_team_service_credential_has_no_owner_to_pass_so_it_stays_the_admins()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var teamToken = await SeedOAuthCredentialAsync(world, null, "at-t", "rt-t", ownership: CredentialOwnership.TeamService).ConfigureAwait(false);

        await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, world.MayaId, teamToken)).ConfigureAwait(false);
        await Should.ThrowAsync<ArgumentException>(() => InitAsync(world, world.BossId, teamToken)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
    }

    [Fact]
    public async Task An_admin_still_reconnects_a_members_credential_and_the_rank_reach_over_a_peer_is_unchanged()
    {
        // credentials.manage is the permission; reaching the person is the second guard, the one every act on a member keeps: an Admin
        // may act on someone below them, never across. Reconnecting in place does not lower it.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var mayas = await SeedOAuthCredentialAsync(world, world.MayaId, "at-m", "rt-m").ConfigureAwait(false);
        var nachos = await SeedOAuthCredentialAsync(world, world.NachoId, "at-n", "rt-n").ConfigureAwait(false);

        (await InitAsync(world, world.BossId, mayas).ConfigureAwait(false)).State.ShouldNotBeNullOrEmpty();
        await Should.ThrowAsync<RoleOutranksActorException>(() => InitAsync(world, world.BossId, nachos)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(1, "only the start that was allowed left a pending connect");
    }

    [Fact]
    public async Task A_credential_of_another_team_is_not_an_owners_way_past_the_permission()
    {
        // The owner shortcut is for a credential of THIS team. Maya owns a credential in another team; naming it from hers gets the
        // permission check she would have got before — she lacks it — rather than the owner's pass.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var elsewhere = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var foreign = await SeedOAuthCredentialAsync(elsewhere, world.MayaId, "at-f", "rt-f").ConfigureAwait(false);

        await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, world.MayaId, foreign)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
    }

    // ─── What a connect that names no credential still needs ─────────────────────────────────────────────────

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public async Task A_plain_connect_still_takes_credentials_manage_even_for_oneself(TeamRole role)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var asker = role == TeamRole.Member ? world.MayaId : world.ViewerId;

        await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, asker, replaceCredentialId: null)).ConfigureAwait(false);
        await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, asker, replaceCredentialId: null, intendedOwnerUserId: asker)).ConfigureAwait(false);

        (await InitAsync(world, world.BossId, replaceCredentialId: null).ConfigureAwait(false)).State.ShouldNotBeNullOrEmpty("an Admin's connect is as it always was");
        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(1);
    }

    [Fact]
    public async Task An_owner_cannot_use_their_own_reconnect_to_connect_for_someone_else()
    {
        // The guard stays: a name given with the credential must be the credential's owner. An owner's pass covers re-authorizing
        // THEIR credential and nothing a connect could do in another person's name.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var mayas = await SeedOAuthCredentialAsync(world, world.MayaId, "at-m", "rt-m").ConfigureAwait(false);

        await Should.ThrowAsync<ArgumentException>(() => InitAsync(world, world.MayaId, mayas, intendedOwnerUserId: world.NachoId)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
    }

    // ─── Departed, retired ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_owner_who_has_left_the_team_cannot_start_a_reconnect_and_an_admin_cannot_start_one_for_them()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var mayas = await SeedOAuthCredentialAsync(world, world.MayaId, "at-m", "rt-m").ConfigureAwait(false);

        await RemoveMembershipAsync(world, world.MayaId).ConfigureAwait(false);

        await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, world.MayaId, mayas)).ConfigureAwait(false);
        await Should.ThrowAsync<KeyNotFoundException>(() => InitAsync(world, world.BossId, mayas)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
    }

    [Fact]
    public async Task A_credential_retired_with_its_person_cannot_be_reconnected_by_them_or_anyone()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var mayas = await SeedOAuthCredentialAsync(world, world.MayaId, "at-m", "rt-m", disconnected: true, retired: true).ConfigureAwait(false);

        // Retired is a refusal of its own, not the permission's: the owner's pass does not reach a row that is no longer live, so the
        // permission check speaks first for someone who lacks it, and the retirement for someone who has it.
        await Should.ThrowAsync<TenantAccessDeniedException>(() => InitAsync(world, world.MayaId, mayas)).ConfigureAwait(false);
        await Should.ThrowAsync<CredentialRetiredException>(() => InitAsync(world, world.BossId, mayas)).ConfigureAwait(false);

        (await CountPendingStatesAsync(world).ConfigureAwait(false)).ShouldBe(0);
    }

    // ─── The callback asks again ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CompletionDoor.BrowserBound)]
    [InlineData(CompletionDoor.Initiator)]
    public async Task A_reconnect_an_admin_started_for_someone_else_is_refused_at_the_end_when_they_are_no_longer_an_admin(CompletionDoor door)
    {
        // The start was allowed on credentials.manage, which is a role: the callback lands up to ten minutes later, and the person who
        // started it may have been demoted since. The same bar is asked again — before the code is exchanged — of the starter's
        // standing now, so a token is never written under a permission that was taken back.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        var init = await InitAsync(world, world.BossId, credentialId).ConfigureAwait(false);
        var before = await LoadCredentialAsync(credentialId).ConfigureAwait(false);

        await SetRoleAsync(world, world.BossId, TeamRole.Member).ConfigureAwait(false);

        await Should.ThrowAsync<OAuthCallbackException>(() => FinishAsync(world, init, world.BossId, "authz:maya", door)).ConfigureAwait(false);

        gitlab.Count(StubEvent.Authorized).ShouldBe(0, "the code was never exchanged");
        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).EncryptedPayload.ShouldBe(before.EncryptedPayload, "nothing was written");
    }

    [Theory]
    [InlineData(CompletionDoor.BrowserBound)]
    [InlineData(CompletionDoor.Initiator)]
    public async Task A_reconnect_an_admin_started_for_someone_else_still_finishes_while_they_remain_an_admin(CompletionDoor door)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        var init = await InitAsync(world, world.BossId, credentialId).ConfigureAwait(false);

        var finished = await FinishAsync(world, init, world.BossId, "authz:maya", door).ConfigureAwait(false);

        finished.CredentialId.ShouldBe(credentialId);
        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Theory]
    [InlineData(CompletionDoor.BrowserBound)]
    [InlineData(CompletionDoor.Initiator)]
    public async Task A_reconnect_an_admin_started_for_someone_else_is_refused_at_the_end_when_they_have_since_left_the_team(CompletionDoor door)
    {
        // Demoting is not the only way to lose the permission: a starter who is no longer in the team at all has no standing to ask the bar
        // of, and must not be read as having some. The code is never exchanged and nothing is written.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        var init = await InitAsync(world, world.BossId, credentialId).ConfigureAwait(false);
        var before = await LoadCredentialAsync(credentialId).ConfigureAwait(false);

        await RemoveMembershipAsync(world, world.BossId).ConfigureAwait(false);

        await Should.ThrowAsync<OAuthCallbackException>(() => FinishAsync(world, init, world.BossId, "authz:maya", door)).ConfigureAwait(false);

        gitlab.Count(StubEvent.Authorized).ShouldBe(0, "the code was never exchanged");
        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).EncryptedPayload.ShouldBe(before.EncryptedPayload, "nothing was written");
    }

    [Theory]
    [InlineData(CompletionDoor.BrowserBound)]
    [InlineData(CompletionDoor.Initiator)]
    public async Task A_reconnect_an_admin_started_is_refused_at_the_end_when_the_credentials_person_has_since_been_promoted_to_their_rank(CompletionDoor door)
    {
        // The start was allowed because the person was below the Admin; a reconnect signs in AS them, so the reach is asked again with the roles as
        // they are now — two Admins never reach across each other, however the second came to be one.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        var init = await InitAsync(world, world.BossId, credentialId).ConfigureAwait(false);
        var before = await LoadCredentialAsync(credentialId).ConfigureAwait(false);

        await SetRoleAsync(world, world.MayaId, TeamRole.Admin).ConfigureAwait(false);

        await Should.ThrowAsync<OAuthCallbackException>(() => FinishAsync(world, init, world.BossId, "authz:maya", door)).ConfigureAwait(false);

        gitlab.Count(StubEvent.Authorized).ShouldBe(0, "the code was never exchanged");
        (await LoadCredentialAsync(credentialId).ConfigureAwait(false)).EncryptedPayload.ShouldBe(before.EncryptedPayload, "nothing was written");
    }

    [Fact]
    public async Task A_reconnect_an_owner_started_is_not_held_to_a_role_at_the_end_only_to_their_standing()
    {
        // The owner's pass is membership, so a demotion changes nothing for them: a Viewer who started it finishes it.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var credentialId = await SeedOAuthCredentialAsync(world, world.MayaId, "at-0", "rt-0", status: CredentialStatus.Error, lastError: "OAuth exchange rejected: invalid_grant").ConfigureAwait(false);
        await SeedIdentityAsync(world, world.MayaId, credentialId, "maya").ConfigureAwait(false);
        var init = await InitAsync(world, world.MayaId, credentialId).ConfigureAwait(false);

        await SetRoleAsync(world, world.MayaId, TeamRole.Viewer).ConfigureAwait(false);

        var finished = await CompleteAsync(world, init, "authz:maya").ConfigureAwait(false);

        finished.CredentialId.ShouldBe(credentialId);
    }

    [Fact]
    public async Task The_reach_over_a_credentials_person_is_decided_by_the_one_rule_for_every_kind_of_person()
    {
        // The agreement a button depends on: PermitsReconnect over the role TeamStanding gives the person is exactly what the service answers
        // an Admin who starts a reconnect for them — for a member of each role, an instance Admin outside the team (who stands as an Owner), an
        // instance Admin who also holds a lower row (the row is read first when acting ON someone, the role first when someone ACTS), and
        // someone with no standing at all.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var owner = await SeedMemberAsync(world, TeamRole.Owner).ConfigureAwait(false);
        var instanceAdminOutside = await SeedUserAsync().ConfigureAwait(false);
        var instanceAdminWithARow = await SeedMemberAsync(world, TeamRole.Member).ConfigureAwait(false);
        var outsider = await SeedUserAsync().ConfigureAwait(false);
        await GrantInstanceAdminAsync(instanceAdminOutside).ConfigureAwait(false);
        await GrantInstanceAdminAsync(instanceAdminWithARow).ConfigureAwait(false);

        var people = new (Guid Id, TeamRole? Standing)[] { (world.MayaId, TeamRole.Member), (world.ViewerId, TeamRole.Viewer), (world.NachoId, TeamRole.Admin), (owner, TeamRole.Owner), (instanceAdminOutside, TeamRole.Owner), (instanceAdminWithARow, TeamRole.Member), (outsider, null) };

        foreach (var (personId, expectedStanding) in people)
        {
            var standing = await StandingOfAsync(world, personId).ConfigureAwait(false);
            standing.ShouldBe(expectedStanding, $"person {personId}");

            var credentialId = await SeedOAuthCredentialAsync(world, personId, "at-p", "rt-p").ConfigureAwait(false);
            var started = await Record.ExceptionAsync(() => InitAsync(world, world.BossId, credentialId)).ConfigureAwait(false);

            (started is null).ShouldBe(CredentialOwnerOrPermission.PermitsReconnect(TeamRole.Admin, ownsThePersonalCredential: false, standing), $"an Admin starting a reconnect for person {personId} ({expectedStanding?.ToString() ?? "no standing"}): {started?.GetType().Name ?? "accepted"}");
        }
    }

    [Fact]
    public async Task A_team_that_is_gone_gives_nobody_a_standing_to_be_acted_on_as()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var instanceAdmin = await SeedUserAsync().ConfigureAwait(false);
        await GrantInstanceAdminAsync(instanceAdmin).ConfigureAwait(false);

        (await StandingOfAsync(world, world.MayaId).ConfigureAwait(false)).ShouldBe(TeamRole.Member, "fixture check: Maya stands in the live team");

        await SoftDeleteTeamAsync(world).ConfigureAwait(false);

        (await StandingOfAsync(world, world.MayaId).ConfigureAwait(false)).ShouldBeNull("the service's hold refuses a team that is gone, so nobody is acted on in it");
        (await StandingOfAsync(world, instanceAdmin).ConfigureAwait(false)).ShouldBeNull();
    }

    private async Task<TeamRole?> StandingOfAsync(World world, Guid personId)
    {
        using var scope = _fixture.BeginScope();

        return await TeamStanding.RoleActedOnAsync(scope.Resolve<CodeSpaceDbContext>(), world.TeamId, personId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SoftDeleteTeamAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Team.Where(t => t.Id == world.TeamId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedDate, DateTimeOffset.UtcNow)).ConfigureAwait(false);
    }

    // ─── The pure rule and TeamStanding's role agree with what the pipeline decides ───────────────────────────

    [Fact]
    public async Task The_role_a_standing_amounts_to_is_the_one_the_membership_resolver_gives_the_caller()
    {
        // The callback is anonymous, so it cannot ask the resolver, which answers for the caller. TeamStanding answers for anyone; the two
        // must never disagree, or the start of a reconnect and its end would judge the same person differently.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab).ConfigureAwait(false);
        var outsider = await SeedUserAsync().ConfigureAwait(false);
        var instanceAdmin = await SeedUserAsync().ConfigureAwait(false);
        var adminWithARowBelowOwner = await SeedMemberAsync(world, TeamRole.Member).ConfigureAwait(false);
        await GrantInstanceAdminAsync(instanceAdmin).ConfigureAwait(false);
        await GrantInstanceAdminAsync(adminWithARowBelowOwner).ConfigureAwait(false);

        var everyone = new[] { world.NachoId, world.MayaId, world.BossId, world.ViewerId, outsider, instanceAdmin, adminWithARowBelowOwner };

        await AssertTheyAgreeAsync(world, everyone, "in a live team").ConfigureAwait(false);

        (await RoleAsCallerAsync(world, instanceAdmin).ConfigureAwait(false)).ShouldBe(TeamRole.Owner, "fixture check: an instance Admin stands as an Owner in every team");
        (await RoleAsCallerAsync(world, adminWithARowBelowOwner).ConfigureAwait(false)).ShouldBe(TeamRole.Owner, "fixture check: and outranks the row they also hold");
        (await RoleAsCallerAsync(world, outsider).ConfigureAwait(false)).ShouldBeNull("fixture check: no row, no role, no standing");

        await SoftDeleteTeamAsync(world).ConfigureAwait(false);

        // A team that is gone denies every member whatever rows outlive it, and only an instance Admin still stands — in this one as in every team.
        await AssertTheyAgreeAsync(world, everyone, "in a deleted team").ConfigureAwait(false);

        (await RoleAsCallerAsync(world, world.MayaId).ConfigureAwait(false)).ShouldBeNull("fixture check: a member is denied once their team is deleted");
    }

    private async Task AssertTheyAgreeAsync(World world, IEnumerable<Guid> users, string when)
    {
        foreach (var userId in users)
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            var asked = await TeamStanding.RoleHeldAsync(db, world.TeamId, userId, CancellationToken.None).ConfigureAwait(false);

            var asCaller = await RoleAsCallerAsync(world, userId).ConfigureAwait(false);

            asked.ShouldBe(asCaller, $"user {userId} {when}");
        }
    }

    public enum CompletionDoor
    {
        /// <summary>The provider's redirect, in the browser that started the connect.</summary>
        BrowserBound,

        /// <summary>The SPA's completion page, for a callback that came back without the binding cookie.</summary>
        Initiator
    }

    private async Task<CompleteCredentialOAuthResult> FinishAsync(World world, InitCredentialOAuthResult init, Guid starter, string code, CompletionDoor door)
    {
        if (door == CompletionDoor.BrowserBound) return await CompleteAsync(world, init, code).ConfigureAwait(false);

        using var scope = Scope(world, starter);

        return await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthAsInitiatorCommand { State = init.State, Code = code }).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(World world, Guid userId, TeamRole role)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == world.TeamId && m.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, role)).ConfigureAwait(false);
    }

    private async Task GrantInstanceAdminAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var roleId = await db.Role.AsNoTracking().Where(r => r.Name == CodeSpace.Messages.Constants.Roles.Admin).Select(r => r.Id).SingleAsync().ConfigureAwait(false);

        db.RoleUser.Add(new CodeSpace.Core.Persistence.Entities.RoleUser { Id = Guid.NewGuid(), RoleId = roleId, UserId = userId });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>What the pipeline's resolver says the person is in this team when they are the caller — null when it refuses them.</summary>
    private async Task<TeamRole?> RoleAsCallerAsync(World world, Guid userId)
    {
        using var scope = Scope(world, userId, configure: null, await RolesOfAsync(userId).ConfigureAwait(false));

        try
        {
            return await scope.Resolve<TeamMembershipResolver>().ResolveRoleAsync(world.TeamId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TenantAccessDeniedException)
        {
            return null;
        }
    }

    private async Task<string[]> RolesOfAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return await db.RoleUser.AsNoTracking().Where(ru => ru.UserId == userId).Join(db.Role.AsNoTracking(), ru => ru.RoleId, r => r.Id, (ru, r) => r.Name).ToArrayAsync().ConfigureAwait(false);
    }
}
