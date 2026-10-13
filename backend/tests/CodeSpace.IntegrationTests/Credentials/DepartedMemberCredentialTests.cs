using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Teams;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Identity;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Commands.Teams;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// What a person's own provider tokens can still do for a team after they stop belonging to it.
///
/// <para>A Personal credential is one person's GitHub/GitLab account lent to the team while they are in it — the
/// contract on <see cref="CredentialOwnership.Personal"/> says it vanishes when they leave. Before this, leaving or being
/// removed deleted only the membership row: the token stayed Active, a Member could still list it and enumerate the
/// repositories it reaches, an Admin could bind more of them through it, and any workflow could act AS the departed
/// person by naming them in <c>actAsUserId</c>. Each test departs a member through the real mediator pipeline and
/// then asks one of those doors.</para>
///
/// <para>Three ways a token could still outlive its person are pinned here too: a connect or token link in flight while
/// they leave (either order — the departure commits during the provider round-trip, or starts once the write holds their
/// membership), a token added without naming an owner, and every departure from before departures retired anything,
/// which the departure sweep retires.</para>
///
/// <para>Real Postgres, the real pipeline and services; only the provider is the Git test provider, which answers
/// whoami and echoes PR opens into <see cref="TestPullRequestOpenCapture"/>, so "never reached the provider" is
/// observed, not inferred.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DepartedMemberCredentialTests
{
    private const int MaxSweepPasses = 20;

    /// <summary>The departure sweep is deployment-wide and this suite shares one database: every earlier test's leftover token of someone not in its team is a candidate ahead of ours, fifty a pass.</summary>
    private const int MaxDepartureSweepPasses = 200;

    private static readonly TimeSpan LockWaitTimeout = TimeSpan.FromSeconds(15);

    private readonly PostgresFixture _fixture;

    public DepartedMemberCredentialTests(PostgresFixture fixture) { _fixture = fixture; }

    public enum Departure { Removed, Left }

    [Theory]
    [InlineData(Departure.Removed)]
    [InlineData(Departure.Left)]
    public async Task Departing_retires_every_token_the_member_owns_in_that_team_and_nothing_else(Departure departure)
    {
        var world = await SeedWorldAsync();

        (await ResolveAsync(world.DepartingId, world.InstanceA)).ShouldNotBeNull("fixture check: before departing, the member's own identity acts on the team's instance");

        await DepartAsync(world, departure);

        var token = await LoadCredentialAsync(world.PersonalCredentialA);
        token.Status.ShouldBe(CredentialStatus.Revoked, $"{departure}: the departed member's token is no longer the team's to use");
        token.EncryptedPayload.ShouldBe(string.Empty, $"{departure}: nothing usable is left at rest");
        token.DeletedDate.ShouldNotBeNull($"{departure}: the token leaves the team's credential list");

        (await LoadIdentityAsync(world.IdentityA)).DeletedDate.ShouldNotBeNull($"{departure}: the act-as identity on the team's instance goes with its token");
        (await ResolveAsync(world.DepartingId, world.InstanceA)).ShouldBeNull($"{departure}: nobody can act as the departed member on this team's instance");

        var personalRepo = await LoadRepositoryAsync(world.PersonalRepository);
        personalRepo.Status.ShouldBe(RepositoryStatus.Error, $"{departure}: a repository bound through the departed member's token says it needs a new credential rather than keep a dead one");
        personalRepo.LastError.ShouldNotBeNullOrWhiteSpace();
        (await LoadRepositoryAsync(world.ConnectionRepository)).Status.ShouldBe(RepositoryStatus.Active, $"{departure}: the team's own connection was never the member's");

        var otherTeamToken = await LoadCredentialAsync(world.PersonalCredentialB);
        otherTeamToken.Status.ShouldBe(CredentialStatus.Active, $"{departure}: the token they lent their OTHER team is theirs, not this team's");
        otherTeamToken.EncryptedPayload.ShouldNotBeEmpty();
        otherTeamToken.DeletedDate.ShouldBeNull();
        (await ResolveAsync(world.DepartingId, world.InstanceB)).ShouldNotBeNull($"{departure}: they still act as themselves in the team they are still in");
    }

    [Fact]
    public async Task An_act_as_write_through_a_departed_member_never_reaches_the_provider()
    {
        var world = await SeedWorldAsync();
        var title = $"opened as a departed member {Guid.NewGuid():N}";

        await DepartAsync(world, Departure.Removed);

        var failure = await Should.ThrowAsync<ActorIdentityRequiredException>(() => OpenPullRequestAsAsync(world, world.DepartingId, title));

        failure.ProviderInstanceId.ShouldBe(world.InstanceA);
        OpenedPullRequestTitles().ShouldNotContain(title, "the write was refused before any provider call — the departed member's token was never presented");
    }

    [Fact]
    public async Task The_team_can_no_longer_list_browse_or_bind_through_a_departed_members_token()
    {
        var world = await SeedWorldAsync();

        (await ListCredentialIdsAsync(world.MemberId, world.TeamA)).ShouldContain(world.PersonalCredentialA, "fixture check: while they are in the team, their token is one of its credentials");

        await DepartAsync(world, Departure.Removed);

        (await ListCredentialIdsAsync(world.MemberId, world.TeamA)).ShouldNotContain(world.PersonalCredentialA, "a Member no longer sees the departed member's token");

        await Should.ThrowAsync<TenantAccessDeniedException>(() => SendAsync(world.MemberId, world.TeamA, new ListAccessibleRepositoriesQuery { CredentialId = world.PersonalCredentialA }));

        await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(world.OwnerId, world.TeamA, new BindRepositoryCommand { ProviderInstanceId = world.InstanceA, CredentialId = world.PersonalCredentialA, ProjectIdentifier = "acme/web" }));
        (await RepositoriesBoundThroughAsync(world.PersonalCredentialA)).ShouldBe(1, "only the repository bound before the departure still names the token — the refused bind wrote nothing");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_repair_sweep_never_moves_a_departed_members_bindings(bool departed, bool moves)
    {
        // The shape the sweep exists to heal: a repository left on a token the member disconnected, and a token they
        // linked after it. The stay-in-the-team row is the control proving the sweep would move it. Departing retires the
        // newer token too, so the member leaves nothing a binding could follow and the repository keeps naming the token
        // that died.
        var world = await SeedWorldAsync();
        var dead = await SeedDisconnectedTokenAsync(world);
        var repoId = await SeedRepositoryAsync(world.TeamA, world.InstanceA, dead, "acme/stranded", RepositoryStatus.Error);

        if (departed) await DepartAsync(world, Departure.Removed);

        await SweepUntilDrainedAsync();

        var repo = await LoadRepositoryAsync(repoId);

        if (!moves)
        {
            repo.CredentialId.ShouldBe(dead, "the departed member's bindings follow nothing — not even the token they linked before leaving");
            repo.Status.ShouldBe(RepositoryStatus.Error);
            return;
        }

        repo.CredentialId.ShouldBe(world.PersonalCredentialA, "fixture check: for a member still in the team, the sweep moves the stranded binding to the token they linked after the disconnect");
        repo.Status.ShouldBe(RepositoryStatus.Active);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_oauth_connect_whose_owner_left_before_the_callback_is_refused(bool startedByThemselves)
    {
        // The provider's callback can land up to the state's lifetime after Init. A member who is removed while a connect
        // in their name is in flight — one they started as an Admin, or one the Owner started for them — must not come
        // back as a fresh token the team can bind through, or that the repair sweep could hand their old bindings to.
        var world = await SeedWorldAsync();
        var oauth = new StubOAuthClient(ProviderKind.Git, new OAuthTokenResponse { AccessToken = "at-after-leaving", RefreshToken = "rt" });

        if (startedByThemselves) await SendAsync(world.OwnerId, world.TeamA, new ChangeTeamMemberRoleCommand { UserId = world.DepartingId, Role = TeamRole.Admin });

        InitCredentialOAuthResult init;
        using (var scope = OAuthScope(oauth, startedByThemselves ? world.DepartingId : world.OwnerId, world.TeamA))
            init = await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceA, DisplayName = "their GitLab", IntendedOwnerUserId = world.DepartingId });

        await DepartAsync(world, Departure.Removed);

        using (var callback = OAuthScope(oauth, userId: null, teamId: null))
            await Should.ThrowAsync<OAuthCallbackException>(() => callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code", BrowserNonce = init.BrowserNonce }));

        (await LiveCredentialsOwnedInAsync(world.TeamA, world.DepartingId)).ShouldBe(0, "no token of the departed member's is live in the team they left");
        oauth.LastExchange.ShouldBeNull("the code is never exchanged, so no token is minted for a connect that will not be kept");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_departure_that_commits_during_the_oauth_code_exchange_leaves_the_departed_member_no_token(bool startedByThemselves)
    {
        // The other side of the callback's window: the owner is still a member when the callback starts, and their removal
        // commits while the provider is minting the token. Persisted, that token would be a live credential of someone
        // outside the team, and the reconnect carry-forward would move the bindings the departure just broke onto it.
        var world = await SeedWorldAsync();
        var oauth = new DepartingDuringExchangeOAuthClient(() => DepartAsync(world, Departure.Removed));

        if (startedByThemselves) await SendAsync(world.OwnerId, world.TeamA, new ChangeTeamMemberRoleCommand { UserId = world.DepartingId, Role = TeamRole.Admin });

        var init = await InitConnectAsync(oauth, startedByThemselves ? world.DepartingId : world.OwnerId, world);

        using (var callback = OAuthScope(oauth, userId: null, teamId: null))
            await Should.ThrowAsync<OAuthCallbackException>(() => callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code", BrowserNonce = init.BrowserNonce }));

        oauth.Exchanged.ShouldBeTrue("fixture check: the departure ran inside the provider's code exchange, after the callback's first membership check");
        (await IsMemberAsync(world.TeamA, world.DepartingId)).ShouldBeFalse("fixture check: the departure committed");
        (await LiveCredentialsOwnedInAsync(world.TeamA, world.DepartingId)).ShouldBe(0, "the token minted while they were leaving is never kept");

        var personalRepo = await LoadRepositoryAsync(world.PersonalRepository);
        personalRepo.CredentialId.ShouldBe(world.PersonalCredentialA, "the repository the departure broke still names the token that died — nothing carried it forward");
        personalRepo.Status.ShouldBe(RepositoryStatus.Error);
    }

    [Fact]
    public async Task A_departure_that_commits_while_their_new_token_is_checked_leaves_the_departed_member_no_token()
    {
        // Linking a token checks it against the provider between the request's membership check and the insert. A
        // departure that commits in that gap must not come back as a live token, nor have its broken bindings carried
        // forward onto one.
        var world = await SeedWorldAsync();
        var probe = new DepartingDuringProbe { DuringProbe = () => DepartAsync(world, Departure.Removed) };

        using (var scope = LinkScope(probe, world.DepartingId, world.TeamA))
            await Should.ThrowAsync<TenantAccessDeniedException>(() => scope.Resolve<IMediator>().Send(new LinkProviderIdentityByPatCommand { ProviderInstanceId = world.InstanceA, AccessToken = "linked-while-leaving" }));

        probe.Probed.ShouldBeTrue("fixture check: the departure ran inside the provider check, after the request's membership check");
        (await IsMemberAsync(world.TeamA, world.DepartingId)).ShouldBeFalse("fixture check: the departure committed");
        (await LiveCredentialsOwnedInAsync(world.TeamA, world.DepartingId)).ShouldBe(0, "the token they were linking as they left is never kept");

        var personalRepo = await LoadRepositoryAsync(world.PersonalRepository);
        personalRepo.CredentialId.ShouldBe(world.PersonalCredentialA, "the repository the departure broke still names the token that died — nothing carried it forward");
        personalRepo.Status.ShouldBe(RepositoryStatus.Error);
    }

    public enum Holder { ConnectCallback, CallerNamingThem }

    [Theory]
    [InlineData(Holder.ConnectCallback, TeamRole.Member)]
    [InlineData(Holder.CallerNamingThem, TeamRole.Member)]
    [InlineData(Holder.ConnectCallback, TeamRole.Owner)]
    public async Task A_departure_waits_for_a_write_that_holds_the_membership_then_retires_what_it_wrote(Holder holder, TeamRole departingRole)
    {
        // The ordering the refusals above cannot cover: the write in their name has already confirmed they are a member
        // when the departure starts, and commits a token for them while the departure waits — as a connect inserts its
        // token at its final save. The departure then retires that token with every other, rather than read the token
        // list before it exists. An Owner's departure locks the team row before the membership row; the write holds both,
        // team row first, so neither ever waits on a row the other holds — holding the membership row alone, the write's
        // insert waits on the team row the departure holds while the departure waits on it, and this case deadlocks.
        var world = await SeedWorldAsync();
        var departureKind = departingRole == TeamRole.Owner ? Departure.Left : Departure.Removed;
        Task departure;
        Guid written;

        if (departingRole == TeamRole.Owner) await SendAsync(world.OwnerId, world.TeamA, new ChangeTeamMemberRoleCommand { UserId = world.DepartingId, Role = TeamRole.Owner });

        using (var writer = holder == Holder.ConnectCallback ? _fixture.BeginScopeAs(null, null) : _fixture.BeginScopeAs(world.OwnerId, world.TeamA))
        {
            var db = writer.Resolve<CodeSpaceDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();

            await HoldMembershipAsync(writer, holder, world);

            departure = DepartAsync(world, departureKind);
            await WaitUntilDepartureWaitsOnALockAsync(departure, holder);

            written = await WriteTokenInTheirNameAsync(db, world);
            await transaction.CommitAsync();
        }

        await departure.WaitAsync(LockWaitTimeout);

        var token = await LoadCredentialAsync(written);
        token.Status.ShouldBe(CredentialStatus.Revoked, $"{holder}/{departingRole}: the token written while the departure waited is retired with the rest");
        token.EncryptedPayload.ShouldBe(string.Empty);
        token.DeletedDate.ShouldNotBeNull();
        (await IsMemberAsync(world.TeamA, world.DepartingId)).ShouldBeFalse($"{holder}/{departingRole}: the departure went through once the write committed");
        (await LiveCredentialsOwnedInAsync(world.TeamA, world.DepartingId)).ShouldBe(0, $"{holder}/{departingRole}: nothing of theirs is live in the team they left");
    }

    public enum Lent { AddedWithoutNamingAnOwner, WrittenOwnerlessBeforeOwnersDefaulted }

    [Theory]
    [InlineData(Lent.AddedWithoutNamingAnOwner)]
    [InlineData(Lent.WrittenOwnerlessBeforeOwnersDefaulted)]
    public async Task A_token_added_without_naming_an_owner_goes_when_whoever_added_it_leaves(Lent lent)
    {
        // A personal token added without an owner used to be stored owner-less, so nothing keyed on its owner — the
        // departure included — ever found it: the person who added it left and the team kept using their token.
        var world = await SeedWorldAsync();

        await SendAsync(world.OwnerId, world.TeamA, new ChangeTeamMemberRoleCommand { UserId = world.DepartingId, Role = TeamRole.Admin });

        var tokenId = lent == Lent.AddedWithoutNamingAnOwner
            ? await SendAsync(world.DepartingId, world.TeamA, new AddCredentialCommand { ProviderInstanceId = world.InstanceA, DisplayName = "my token", Payload = new PatPayload { Token = "glpat-mine" } })
            : await SeedOwnerlessTokenAsync(world, createdBy: world.DepartingId);

        if (lent == Lent.AddedWithoutNamingAnOwner)
            (await LoadCredentialAsync(tokenId)).OwnerUserId.ShouldBe(world.DepartingId, "a personal token added without naming an owner is the caller's own");

        (await ListCredentialIdsAsync(world.MemberId, world.TeamA)).ShouldContain(tokenId, "fixture check: while they are in the team, the token is one of its credentials");

        await DepartAsync(world, Departure.Left);

        var token = await LoadCredentialAsync(tokenId);
        token.Status.ShouldBe(CredentialStatus.Revoked, $"{lent}: the token goes with the person who added it");
        token.EncryptedPayload.ShouldBe(string.Empty);
        token.DeletedDate.ShouldNotBeNull();
        (await ListCredentialIdsAsync(world.MemberId, world.TeamA)).ShouldNotContain(tokenId, $"{lent}: a Member no longer sees it");
        await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(world.OwnerId, world.TeamA, new BindRepositoryCommand { ProviderInstanceId = world.InstanceA, CredentialId = tokenId, ProjectIdentifier = "acme/web" }));
    }

    [Fact]
    public async Task A_member_who_left_before_departures_retired_tokens_loses_every_token_they_left_behind_to_the_sweep()
    {
        // Departures used to delete the membership row and nothing else, and an older pod still does during a rolling
        // deploy. Every such person's tokens are still live in the team they left — listed, browsable, bindable — until
        // the sweep retires them exactly as a departure now does.
        var world = await SeedWorldAsync();
        var ownerless = await SeedOwnerlessTokenAsync(world, createdBy: world.DepartingId);
        var ownerlessOfAMember = await SeedOwnerlessTokenAsync(world, createdBy: world.MemberId);
        var membersIdentity = await LinkTokenAsync(world.MemberId, world.TeamA, world.InstanceA, "member-token");
        var membersToken = await IdentityCredentialAsync(membersIdentity);

        await DeleteMembershipAsDeparturesUsedToAsync(world.TeamA, world.DepartingId);

        (await ListCredentialIdsAsync(world.MemberId, world.TeamA)).ShouldContain(world.PersonalCredentialA, "fixture check: a departure from before left the token live and listed");
        (await LoadRepositoryAsync(world.PersonalRepository)).Status.ShouldBe(RepositoryStatus.Active, "fixture check: and the repository bound through it working");

        await SweepDepartedUntilRetiredAsync(world.PersonalCredentialA, ownerless);

        foreach (var retiredId in new[] { world.PersonalCredentialA, ownerless })
        {
            var retired = await LoadCredentialAsync(retiredId);
            retired.Status.ShouldBe(CredentialStatus.Revoked, "every token the departed member lent the team is retired");
            retired.EncryptedPayload.ShouldBe(string.Empty);
            retired.DeletedDate.ShouldNotBeNull();
        }

        (await LoadIdentityAsync(world.IdentityA)).DeletedDate.ShouldNotBeNull("the act-as identity goes with its token");

        var personalRepo = await LoadRepositoryAsync(world.PersonalRepository);
        personalRepo.Status.ShouldBe(RepositoryStatus.Error, "a repository bound through a retired token says it needs a new credential");
        personalRepo.LastError.ShouldNotBeNullOrWhiteSpace();

        (await ListCredentialIdsAsync(world.MemberId, world.TeamA)).ShouldNotContain(world.PersonalCredentialA, "a Member no longer sees the departed member's token");
        await Should.ThrowAsync<TenantAccessDeniedException>(() => SendAsync(world.MemberId, world.TeamA, new ListAccessibleRepositoriesQuery { CredentialId = world.PersonalCredentialA }));
        await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(world.OwnerId, world.TeamA, new BindRepositoryCommand { ProviderInstanceId = world.InstanceA, CredentialId = world.PersonalCredentialA, ProjectIdentifier = "acme/web" }));

        foreach (var keptId in new[] { membersToken, ownerlessOfAMember, world.PersonalCredentialB })
            (await LoadCredentialAsync(keptId)).Status.ShouldBe(CredentialStatus.Active, "a token lent by someone still in its team is untouched — including the departed member's in the team they are still in");

        (await LoadRepositoryAsync(world.ConnectionRepository)).Status.ShouldBe(RepositoryStatus.Active, "the team's own connection was never the member's");
    }

    [Fact]
    public async Task What_an_instance_admin_lends_a_team_they_are_not_in_stays_the_teams_until_they_lose_the_role()
    {
        // Everywhere else a live instance Admin stands in every team as its Owner does, and may lend a team a token without
        // joining it. A membership row is not the whole answer: read as one, the sweep retired every such token on deploy
        // and broke every repository bound through it. Once the role goes, the person has no standing left, and the sweep
        // retires what they lent exactly as it retires a departed member's.
        var world = await SeedWorldAsync();
        var adminId = await SeedInstanceAdminAsync();
        var linked = await IdentityCredentialAsync(await LinkTokenAsync(adminId, world.TeamA, world.InstanceA, "admins-token", Roles.Admin));
        var added = await SendAsync(adminId, world.TeamA, new AddCredentialCommand { ProviderInstanceId = world.InstanceA, DisplayName = "admin's token", Payload = new PatPayload { Token = "glpat-admin" } }, Roles.Admin);
        var repoId = await SeedRepositoryAsync(world.TeamA, world.InstanceA, linked, "acme/lent-by-an-admin", RepositoryStatus.Active);
        var title = $"opened as an instance admin {Guid.NewGuid():N}";

        (await LoadCredentialAsync(added)).OwnerUserId.ShouldBe(adminId, "fixture check: a token the Admin added without naming an owner is theirs");

        await DeleteMembershipAsDeparturesUsedToAsync(world.TeamA, world.DepartingId);
        await SweepDepartedUntilRetiredAsync(world.PersonalCredentialA);
        await DrainDepartureSweepAsync();

        foreach (var keptId in new[] { linked, added })
            (await LoadCredentialAsync(keptId)).Status.ShouldBe(CredentialStatus.Active, "an instance Admin has standing in every team, so what they lent one is not a departed person's token");

        (await LoadRepositoryAsync(repoId)).Status.ShouldBe(RepositoryStatus.Active, "the repository bound through the Admin's token keeps working across the sweep");
        await OpenPullRequestAsAsync(world, adminId, title);
        OpenedPullRequestTitles().ShouldContain(title, "a workflow in the team still acts as the instance Admin");

        await RevokeInstanceAdminAsync(adminId);
        await SweepDepartedUntilRetiredAsync(linked, added);

        var repo = await LoadRepositoryAsync(repoId);
        repo.Status.ShouldBe(RepositoryStatus.Error, "once the role is gone, the repository bound through their token says it needs a new credential");
        repo.LastError.ShouldNotBeNullOrWhiteSpace();
        (await ResolveAsync(adminId, world.InstanceA)).ShouldBeNull("nobody acts as an ex-Admin in a team they were never in");
        await Should.ThrowAsync<ActorIdentityRequiredException>(() => OpenPullRequestAsAsync(world, adminId, $"{title} after the role went"));
    }

    public enum Unattributable { CreatorIsEmpty, CreatorNamesNoAccount }

    [Theory]
    [InlineData(Unattributable.CreatorIsEmpty)]
    [InlineData(Unattributable.CreatorNamesNoAccount)]
    public async Task The_sweep_never_retires_a_token_it_cannot_attribute_to_an_account(Unattributable unattributable)
    {
        // An owner-less token belongs, for retirement, to whoever added it — when that is someone. A creator stamp that is
        // empty, or names no account (the column has no foreign key: a script could write anything), says nothing about
        // who lent the token or whether they are still in the team. Retiring on that would be a guess, and a wrong one
        // breaks every repository bound through it with no departure to explain it.
        var world = await SeedWorldAsync();
        var tokenId = await SeedOwnerlessTokenAsync(world, createdBy: Guid.NewGuid());

        if (unattributable == Unattributable.CreatorIsEmpty) await StampCreatorAsync(tokenId, Guid.Empty);

        await DeleteMembershipAsDeparturesUsedToAsync(world.TeamA, world.DepartingId);
        await SweepDepartedUntilRetiredAsync(world.PersonalCredentialA);
        await DrainDepartureSweepAsync();

        var token = await LoadCredentialAsync(tokenId);
        token.Status.ShouldBe(CredentialStatus.Active, $"{unattributable}: a token nobody can be named for is kept");
        token.EncryptedPayload.ShouldNotBeEmpty();
        token.DeletedDate.ShouldBeNull();
    }

    [Theory]
    [InlineData(Departure.Removed)]
    [InlineData(Departure.Left)]
    public async Task An_instance_admin_who_leaves_a_team_keeps_what_they_lent_it(Departure departure)
    {
        // Leaving takes the membership row, not the role: an instance Admin still stands in the team afterwards, as in
        // every other, so a departure has nothing of theirs to retire.
        var world = await SeedWorldAsync();
        await GrantInstanceAdminAsync(world.DepartingId);

        await DepartAsync(world, departure);

        (await IsMemberAsync(world.TeamA, world.DepartingId)).ShouldBeFalse($"fixture check: {departure} took the membership row");
        (await LoadCredentialAsync(world.PersonalCredentialA)).Status.ShouldBe(CredentialStatus.Active, $"{departure}: the token an instance Admin lent the team is still the team's");
        (await LoadRepositoryAsync(world.PersonalRepository)).Status.ShouldBe(RepositoryStatus.Active, $"{departure}: and the repository bound through it keeps working");
        (await ResolveAsync(world.DepartingId, world.InstanceA)).ShouldNotBeNull($"{departure}: a workflow still acts as them");
    }

    [Fact]
    public async Task An_instance_admin_outside_the_team_connects_their_own_account_through_oauth()
    {
        var world = await SeedWorldAsync();
        var adminId = await SeedInstanceAdminAsync();
        var oauth = new StubOAuthClient(ProviderKind.Git, new OAuthTokenResponse { AccessToken = "admins-oauth-token", RefreshToken = "rt" });

        var init = await InitConnectAsync(oauth, adminId, world, ownerUserId: null, Roles.Admin);

        CompleteCredentialOAuthResult completed;
        using (var callback = OAuthScope(oauth, userId: null, teamId: null))
            completed = await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code", BrowserNonce = init.BrowserNonce });

        var token = await LoadCredentialAsync(completed.CredentialId.ShouldNotBeNull("the starting browser's callback completes the connect"));
        token.OwnerUserId.ShouldBe(adminId, "the connect is the instance Admin's own");
        token.Status.ShouldBe(CredentialStatus.Active, "an instance Admin has standing in the team, so the callback keeps their token");
        var identity = await ResolveAsync(adminId, world.InstanceA);
        identity.ShouldNotBeNull("the connect links the instance Admin's act-as identity in the team");
        identity.CredentialId.ShouldBe(token.Id, "through the token they just connected");
    }

    [Fact]
    public async Task A_demotion_that_commits_during_the_oauth_code_exchange_leaves_the_ex_admin_no_token()
    {
        // The callback re-asks standing once the provider answers. Taking the Admin role away is not an operation of this
        // app — role_user is written by migrations and by hand — so there is no departure to wait on, only the answer at
        // that moment: an ex-Admin outside the team is refused there, and the sweep catches a demotion that lands after it.
        var world = await SeedWorldAsync();
        var adminId = await SeedInstanceAdminAsync();
        var oauth = new DepartingDuringExchangeOAuthClient(() => RevokeInstanceAdminAsync(adminId));

        var init = await InitConnectAsync(oauth, adminId, world, ownerUserId: null, Roles.Admin);

        using (var callback = OAuthScope(oauth, userId: null, teamId: null))
            await Should.ThrowAsync<OAuthCallbackException>(() => callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code", BrowserNonce = init.BrowserNonce }));

        oauth.Exchanged.ShouldBeTrue("fixture check: the role went inside the provider's code exchange");
        (await LiveCredentialsOwnedInAsync(world.TeamA, adminId)).ShouldBe(0, "no token of someone without standing in the team is kept");
    }

    // ── Flow steps ────────────────────────────────────────────────────────────────

    private async Task DepartAsync(World world, Departure departure)
    {
        if (departure == Departure.Removed)
        {
            await SendAsync(world.OwnerId, world.TeamA, new RemoveTeamMemberCommand { UserId = world.DepartingId });
            return;
        }

        await SendAsync(world.DepartingId, world.TeamA, new LeaveTeamCommand());
    }

    private async Task<RemotePullRequest> OpenPullRequestAsAsync(World world, Guid actorUserId, string title)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IPullRequestService>().OpenPullRequestAsync(world.ConnectionRepository, world.TeamA, new OpenPullRequestInput { Title = title, SourceBranch = "feature", TargetBranch = "main" }, actorUserId, CancellationToken.None);
    }

    private IReadOnlyList<string> OpenedPullRequestTitles()
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<TestPullRequestOpenCapture>().All.Select(input => input.Title).ToList();
    }

    private async Task<IReadOnlyList<Guid>> ListCredentialIdsAsync(Guid userId, Guid teamId)
    {
        var credentials = await SendAsync(userId, teamId, new ListCredentialsQuery());

        return credentials.Select(c => c.Id).ToList();
    }

    /// <summary>The recurring repair's tick, through the real mediator pipeline, until a pass moves nothing — the sweep is deployment-wide and the suite shares one database.</summary>
    private async Task SweepUntilDrainedAsync()
    {
        for (var pass = 1; pass <= MaxSweepPasses; pass++)
        {
            using var scope = _fixture.BeginScope();

            if (await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand()) == 0) return;
        }

        throw new ShouldAssertException($"RepairDisconnectedCredentialBindingsCommand still repaired bindings after {MaxSweepPasses} passes — check that the sweep's candidate query drops a credential once nothing live is bound to it.");
    }

    /// <summary>The departure sweep's tick, through the real mediator pipeline, until every named token is retired — never until the deployment-wide backlog drains, which other tests' leftovers in this shared database decide.</summary>
    private async Task SweepDepartedUntilRetiredAsync(params Guid[] credentialIds)
    {
        for (var pass = 1; pass <= MaxDepartureSweepPasses; pass++)
        {
            using (var scope = _fixture.BeginScope())
                await scope.Resolve<IMediator>().Send(new RetireDepartedMembersCredentialsCommand());

            if (await AllRetiredAsync(credentialIds)) return;
        }

        throw new ShouldAssertException($"RetireDepartedMembersCredentialsCommand had not retired {string.Join(", ", credentialIds)} after {MaxDepartureSweepPasses} passes — check that the sweep's candidate query finds a live Personal token whose owner (or, owner-less, whose creator) holds no membership in its team.");
    }

    /// <summary>What departures did before they retired anything: the membership row goes, and nothing else does.</summary>
    private async Task DeleteMembershipAsDeparturesUsedToAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == teamId && m.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// The departure sweep's tick until a pass retires nothing, so every candidate it has has been retired — for asserting
    /// a token is NOT one. Called after a control token is retired, which proves the passes reach this test's rows.
    /// </summary>
    private async Task DrainDepartureSweepAsync()
    {
        for (var pass = 1; pass <= MaxDepartureSweepPasses; pass++)
        {
            using var scope = _fixture.BeginScope();

            if (await scope.Resolve<IMediator>().Send(new RetireDepartedMembersCredentialsCommand()) == 0) return;
        }

        throw new ShouldAssertException($"RetireDepartedMembersCredentialsCommand still retired tokens after {MaxDepartureSweepPasses} passes — check that a retired token stops being a candidate.");
    }

    /// <summary>An account holding the instance Admin role through a <c>role_user</c> row, and no membership anywhere.</summary>
    private async Task<Guid> SeedInstanceAdminAsync()
    {
        var admin = NewUser("instance-admin", Guid.NewGuid().ToString("N")[..8]);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            db.User.Add(admin);
            await db.SaveChangesAsync();
        }

        await GrantInstanceAdminAsync(admin.Id);

        return admin.Id;
    }

    private async Task GrantInstanceAdminAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = await db.Role.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync(), UserId = userId });
        await db.SaveChangesAsync();
    }

    /// <summary>How the role is taken away: nothing in the app writes <c>role_user</c>, so an operator deletes the row.</summary>
    private async Task RevokeInstanceAdminAsync(Guid userId)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().RoleUser.Where(r => r.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>Rewrites the creator stamp past the audit pipeline, which fills an empty one in on insert.</summary>
    private async Task StampCreatorAsync(Guid credentialId, Guid createdBy)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Credential.Where(c => c.Id == credentialId).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedBy, createdBy));
    }

    private static async Task HoldMembershipAsync(ILifetimeScope writer, Holder holder, World world)
    {
        var members = writer.Resolve<ITeamMemberService>();

        if (holder == Holder.CallerNamingThem)
        {
            await members.EnsureMayActOnAsync(world.DepartingId, CancellationToken.None);
            return;
        }

        (await members.HoldMembershipAsync(world.TeamA, world.DepartingId, CancellationToken.None)).ShouldBeTrue("fixture check: they are a member when the write starts");
    }

    /// <summary>A live token owned by the departing member, written — not yet committed — inside the writer's transaction, as a connect writes one.</summary>
    private static async Task<Guid> WriteTokenInTheirNameAsync(CodeSpaceDbContext db, World world)
    {
        var token = new Credential { Id = Guid.NewGuid(), TeamId = world.TeamA, ProviderInstanceId = world.InstanceA, OwnerUserId = world.DepartingId, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = "connected as they left", EncryptedPayload = "sealed", Status = CredentialStatus.Active };

        db.Credential.Add(token);
        await db.SaveChangesAsync();

        return token.Id;
    }

    /// <summary>
    /// Until the departure's own connection is waiting on a row lock. Either it waits — the write's hold is what it is
    /// waiting on, since nothing else in this database holds one — or it finishes first, which means it read what to
    /// retire without waiting for the write and the token the write is about to commit is never retired.
    /// </summary>
    private async Task WaitUntilDepartureWaitsOnALockAsync(Task departure, Holder holder)
    {
        var deadline = DateTime.UtcNow + LockWaitTimeout;

        while (DateTime.UtcNow < deadline)
        {
            departure.IsCompleted.ShouldBeFalse($"{holder}: the departure finished while a write in the member's name still held their membership — it did not wait for it. Faulted: {departure.Exception?.GetBaseException().Message}");

            if (await BackendsWaitingOnALockAsync() > 0) return;

            await Task.Delay(50);
        }

        throw new ShouldAssertException($"{holder}: the departure neither finished nor waited on a lock within {LockWaitTimeout} — check pg_stat_activity for a backend whose wait_event_type is 'Lock' and what it is running.");
    }

    private async Task<int> BackendsWaitingOnALockAsync()
    {
        using var scope = _fixture.BeginScope();

        return (await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").ToListAsync()).Single();
    }

    private async Task<T> SendAsync<T>(Guid userId, Guid teamId, IRequest<T> request, params string[] roles)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, roles);

        return await scope.Resolve<IMediator>().Send(request);
    }

    private async Task<InitCredentialOAuthResult> InitConnectAsync(IOAuthClient oauth, Guid initiatorId, World world) => await InitConnectAsync(oauth, initiatorId, world, world.DepartingId);

    private async Task<InitCredentialOAuthResult> InitConnectAsync(IOAuthClient oauth, Guid initiatorId, World world, Guid? ownerUserId, params string[] roles)
    {
        using var scope = OAuthScope(oauth, initiatorId, world.TeamA, roles);

        return await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = world.InstanceA, DisplayName = "their GitLab", IntendedOwnerUserId = ownerUserId });
    }

    private ILifetimeScope OAuthScope(IOAuthClient oauth, Guid? userId, Guid? teamId, params string[] roles) =>
        _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", roles)).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            b.RegisterInstance<IOAuthClientRegistry>(new OneClientRegistry(oauth)).SingleInstance();
        });

    /// <summary>The real link flow as <paramref name="userId"/>, with only the provider's token check swapped.</summary>
    private ILifetimeScope LinkScope(ICredentialProbeCapability probe, Guid userId, Guid teamId) =>
        _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test")).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            b.RegisterInstance<IProviderRegistry>(new ProviderRegistry(new IProviderCapability[] { probe })).SingleInstance();
        });

    private sealed class OneClientRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;

        public OneClientRegistry(IOAuthClient client) { _client = client; }

        public IOAuthClient Get(ProviderKind kind) => _client;
    }

    /// <summary>A provider whose code exchange takes long enough for <paramref name="depart"/> to commit inside it — run there, and awaited, before the token comes back.</summary>
    private sealed class DepartingDuringExchangeOAuthClient : IOAuthClient
    {
        private readonly Func<Task> _depart;

        public DepartingDuringExchangeOAuthClient(Func<Task> depart) { _depart = depart; }

        public ProviderKind Kind => ProviderKind.Git;

        public bool Exchanged { get; private set; }

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => new($"https://oauth.stub/authorize?state={input.State}");

        public async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken)
        {
            Exchanged = true;
            await _depart();

            return new OAuthTokenResponse { AccessToken = "at-minted-while-leaving", RefreshToken = "rt" };
        }

        public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken) => throw new NotSupportedException("no test here refreshes a token");

        public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A provider token check during which <see cref="DuringProbe"/> runs — and is awaited — before it answers that the
    /// token is valid. Parameterless, and answering exactly as <see cref="TestRepositoryProvider"/> does when nothing is set:
    /// the fixture's capability scan activates every capability in this assembly into the real registry, so this one sits
    /// beside the test provider in every test.
    /// </summary>
    private sealed class DepartingDuringProbe : ICredentialProbeCapability
    {
        public Func<Task>? DuringProbe { get; init; }

        public ProviderKind Kind => ProviderKind.Git;

        public bool Probed { get; private set; }

        public async Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken)
        {
            Probed = true;

            if (DuringProbe != null) await DuringProbe();

            return new CredentialProbeResult { IsValid = true, AuthenticatedUserExternalId = "test-user-id", AuthenticatedUserName = "Test User" };
        }
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────

    private async Task<UserProviderIdentity?> ResolveAsync(Guid userId, Guid instanceId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IActorIdentityResolver>().ResolveAsync(userId, instanceId, CancellationToken.None);
    }

    private async Task<Credential> LoadCredentialAsync(Guid id)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private async Task<UserProviderIdentity> LoadIdentityAsync(Guid id)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().UserProviderIdentity.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private async Task<Repository> LoadRepositoryAsync(Guid id)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<int> RepositoriesBoundThroughAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Repository.AsNoTracking().CountAsync(r => r.CredentialId == credentialId && r.DeletedDate == null);
    }

    private async Task<int> LiveCredentialsOwnedInAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().CountAsync(c => c.TeamId == teamId && c.OwnerUserId == userId && c.Status == CredentialStatus.Active && c.DeletedDate == null);
    }

    private async Task<bool> IsMemberAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().TeamMembership.AsNoTracking().AnyAsync(m => m.TeamId == teamId && m.UserId == userId);
    }

    private async Task<bool> AllRetiredAsync(Guid[] credentialIds)
    {
        using var scope = _fixture.BeginScope();

        var retired = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().CountAsync(c => credentialIds.Contains(c.Id) && c.Status == CredentialStatus.Revoked && c.DeletedDate != null);

        return retired == credentialIds.Length;
    }

    // ── World ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Team A (Owner, Member, and the departing member as a Member) and team B, the departing member's own (they are its
    /// Owner). Each team has a Git instance. Team A's connection repository is bound through a team-service token; its
    /// other repository through the departing member's own token, as an Admin binding through it leaves it. The departing
    /// member linked a token on BOTH instances through the real link flow.
    /// </summary>
    private sealed record World(Guid TeamA, Guid TeamB, Guid OwnerId, Guid MemberId, Guid DepartingId, Guid InstanceA, Guid InstanceB, Guid ConnectionRepository, Guid PersonalRepository, Guid PersonalCredentialA, Guid IdentityA, Guid PersonalCredentialB);

    private async Task<World> SeedWorldAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var owner = NewUser("owner", suffix);
        var member = NewUser("member", suffix);
        var departing = NewUser("departing", suffix);
        var teamA = new Team { Id = Guid.NewGuid(), Slug = $"a-{suffix}", Name = "Team A", Kind = TeamKind.Workspace };
        var teamB = new Team { Id = Guid.NewGuid(), Slug = $"b-{suffix}", Name = "Team B", Kind = TeamKind.Workspace };
        Guid instanceA, instanceB, connectionRepo;

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var encryptor = scope.Resolve<IPayloadEncryptor>();

            db.User.AddRange(owner, member, departing);
            db.Team.AddRange(teamA, teamB);
            db.TeamMembership.AddRange(Membership(teamA, owner, TeamRole.Owner), Membership(teamA, member, TeamRole.Member), Membership(teamA, departing, TeamRole.Member), Membership(teamB, departing, TeamRole.Owner));

            var a = NewInstance(teamA, suffix + "a", encryptor);
            var b = NewInstance(teamB, suffix + "b", encryptor);
            var connection = new Credential { Id = Guid.NewGuid(), TeamId = teamA.Id, ProviderInstanceId = a.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "team-token" })), Status = CredentialStatus.Active };

            db.ProviderInstance.AddRange(a, b);
            db.Credential.Add(connection);
            await db.SaveChangesAsync();

            (instanceA, instanceB) = (a.Id, b.Id);
            connectionRepo = await SeedRepositoryAsync(teamA.Id, a.Id, connection.Id, "acme/api", RepositoryStatus.Active);
        }

        var identityA = await LinkTokenAsync(departing.Id, teamA.Id, instanceA, "departing-token-a");
        await LinkTokenAsync(departing.Id, teamB.Id, instanceB, "departing-token-b");

        var personalA = await IdentityCredentialAsync(identityA);
        var personalB = await LiveCredentialOwnedOnAsync(instanceB, departing.Id);
        var personalRepo = await SeedRepositoryAsync(teamA.Id, instanceA, personalA, "acme/personal", RepositoryStatus.Active);

        return new World(teamA.Id, teamB.Id, owner.Id, member.Id, departing.Id, instanceA, instanceB, connectionRepo, personalRepo, personalA, identityA, personalB);
    }

    /// <summary>A token the departing member connected thirty days ago and disconnected twenty days ago — revoked and blanked, as a disconnect leaves it.</summary>
    private async Task<Guid> SeedDisconnectedTokenAsync(World world)
    {
        var connected = DateTimeOffset.UtcNow.AddDays(-30);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var dead = new Credential { Id = Guid.NewGuid(), TeamId = world.TeamA, ProviderInstanceId = world.InstanceA, OwnerUserId = world.DepartingId, AuthType = AuthType.OAuth, DisplayName = "disconnected", Status = CredentialStatus.Revoked, EncryptedPayload = string.Empty, CreatedDate = connected, LastModifiedDate = connected.AddDays(10) };

        db.Credential.Add(dead);
        await db.SaveChangesAsync();

        return dead.Id;
    }

    /// <summary>
    /// A personal token with no owner, as adding one without naming an owner stored it before the owner defaulted to the
    /// caller: Personal, owner-less, created by <paramref name="createdBy"/>. Production wrote exactly this row through
    /// <c>POST /api/credentials/pat</c> without <c>ownerUserId</c>.
    /// </summary>
    private async Task<Guid> SeedOwnerlessTokenAsync(World world, Guid createdBy)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var token = new Credential { Id = Guid.NewGuid(), TeamId = world.TeamA, ProviderInstanceId = world.InstanceA, Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "added without an owner", EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "glpat-ownerless" })), Status = CredentialStatus.Active, CreatedBy = createdBy, LastModifiedBy = createdBy };

        db.Credential.Add(token);
        await db.SaveChangesAsync();

        return token.Id;
    }

    private async Task<Guid> LinkTokenAsync(Guid userId, Guid teamId, Guid instanceId, string token, params string[] roles)
    {
        var linked = await SendAsync(userId, teamId, new LinkProviderIdentityByPatCommand { ProviderInstanceId = instanceId, AccessToken = token }, roles);

        return linked.Id;
    }

    private async Task<Guid> IdentityCredentialAsync(Guid identityId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().UserProviderIdentity.AsNoTracking().Where(i => i.Id == identityId).Select(i => i.CredentialId).SingleAsync();
    }

    private async Task<Guid> LiveCredentialOwnedOnAsync(Guid instanceId, Guid userId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Where(c => c.ProviderInstanceId == instanceId && c.OwnerUserId == userId && c.DeletedDate == null).Select(c => c.Id).SingleAsync();
    }

    private async Task<Guid> SeedRepositoryAsync(Guid teamId, Guid instanceId, Guid credentialId, string fullPath, RepositoryStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var name = fullPath.Split('/')[1];
        var repo = new Repository { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = $"ext-{Guid.NewGuid():N}", NamespacePath = "acme", Name = name, FullPath = fullPath, DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://git.test/{fullPath}", Status = status };

        db.Repository.Add(repo);
        await db.SaveChangesAsync();

        return repo.Id;
    }

    private static User NewUser(string label, string suffix) => new() { Id = Guid.NewGuid(), Email = $"{label}-{suffix}@departed.test", Name = $"{label}-{suffix}" };

    private static TeamMembership Membership(Team team, User user, TeamRole role) => new() { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = role };

    private static ProviderInstance NewInstance(Team team, string suffix, IPayloadEncryptor encryptor) =>
        new() { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "Git", BaseUrl = $"https://git-{suffix}.test", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret") };
}
