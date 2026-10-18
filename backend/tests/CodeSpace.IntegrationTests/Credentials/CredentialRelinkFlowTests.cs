using System.Text.Json;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Teams;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Recovering what a dead credential left behind, in one action, through the real mediator pipeline: a member who leaves
/// takes their token with them (the team retires it, soft-deleted and out of the credential list), and every repository
/// and group hook bound through it stays pointed at a row that can never authenticate. Nothing carries them forward — the
/// person is gone — and the only repair there was is the per-repository Re-link, which never touched a group hook.
///
/// <para>Real Postgres; the production pipeline and services; the provider is the fixture's Git test provider, wrapped
/// where a test needs it to refuse or to say who it was asked as.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialRelinkFlowTests
{
    private readonly BindingMoveKit _kit;

    public CredentialRelinkFlowTests(PostgresFixture fixture) { _kit = new BindingMoveKit(fixture); }

    [Fact]
    public async Task An_admin_moves_everything_a_departed_member_left_to_one_of_their_own_connections()
    {
        var world = await _kit.SeedWorldAsync();
        var leaver = await _kit.SeedMemberAsync(world, TeamRole.Member);
        var theirs = await _kit.SeedCredentialAsync(world, leaver, "glpat-leaver");
        var first = await _kit.SeedRepositoryAsync(world, theirs);
        var second = await _kit.SeedRepositoryAsync(world, theirs);
        var stranded = await _kit.SeedConnectionWebhookAsync(world, theirs, RepositoryWebhookRegistrationStatus.DeadLettered);
        var adminsOwn = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin");

        await RemoveFromTeamAsync(world, leaver);

        (await _kit.LoadCredentialAsync(theirs)).DeletedDate.ShouldNotBeNull("fixture check: the departure retired the token — soft-deleted, out of the credential list");
        (await _kit.LoadRepositoryAsync(first)).Status.ShouldBe(RepositoryStatus.Error, "fixture check: and stranded what was bound through it");

        using var dispatches = _kit.Jobs().ManualExecution();
        var result = await RelinkAsync(world, world.AdminId, theirs, adminsOwn);

        result.MovedRepositoryCount.ShouldBe(2);
        result.MovedConnectionWebhookCount.ShouldBe(1);

        foreach (var repoId in new[] { first, second })
        {
            var repo = await _kit.LoadRepositoryAsync(repoId);

            repo.CredentialId.ShouldBe(adminsOwn);
            repo.Status.ShouldBe(RepositoryStatus.Active);
            repo.LastError.ShouldBeNull();
            (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-admin", "the next run clones through the credential that took over");
        }

        var hook = await _kit.LoadConnectionWebhookAsync(stranded);

        hook.CredentialId.ShouldBe(adminsOwn);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued, "a hook the dead credential's failures buried is revived");
        _kit.WasDispatched(stranded).ShouldBeTrue("and handed to the registrar once the relink committed");
    }

    [Theory]
    [InlineData(CredentialOwnership.Personal)]
    [InlineData(CredentialOwnership.TeamService)]
    public async Task The_target_may_be_the_callers_own_connection_or_a_team_token(CredentialOwnership targetKind)
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = targetKind == CredentialOwnership.Personal
            ? await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target")
            : await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-target", ownership: CredentialOwnership.TeamService);

        await RelinkAsync(world, world.OwnerId, dead, target);

        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(target);
        (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-target");
    }

    [Fact]
    public async Task Relinking_again_moves_nothing_the_second_time_and_never_touches_a_binding_elsewhere()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var elsewhere = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-elsewhere");
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target");
        var stranded = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var untouched = await _kit.SeedRepositoryAsync(world, elsewhere);
        var removed = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        await SoftDeleteRepositoryAsync(removed);

        var first = await RelinkAsync(world, world.OwnerId, dead, target);
        var second = await RelinkAsync(world, world.OwnerId, dead, target);

        first.MovedRepositoryCount.ShouldBe(1, "only the live repository bound through the dead credential");
        second.MovedRepositoryCount.ShouldBe(0);
        second.MovedConnectionWebhookCount.ShouldBe(0);
        (await _kit.LoadRepositoryAsync(stranded)).CredentialId.ShouldBe(target);
        (await _kit.LoadRepositoryAsync(untouched)).CredentialId.ShouldBe(elsewhere, "a repository bound through another credential is nobody's to move");
        (await _kit.LoadRepositoryAsync(removed)).CredentialId.ShouldBe(dead, "an unbound repository is not live and stays as it was");
    }

    public enum Refusal { SourceStillConnected, TargetIsAnotherPersonsConnection, TargetOnAnotherInstance, TargetRevoked, TargetIsTheSource, SourceInAnotherTeam, TargetInAnotherTeam, NoSuchTarget, TargetRetired }

    [Theory]
    [InlineData(Refusal.SourceStillConnected, typeof(InvalidOperationException))]
    [InlineData(Refusal.TargetIsAnotherPersonsConnection, typeof(InvalidOperationException))]
    [InlineData(Refusal.TargetOnAnotherInstance, typeof(InvalidOperationException))]
    [InlineData(Refusal.TargetRevoked, typeof(InvalidOperationException))]
    [InlineData(Refusal.TargetIsTheSource, typeof(InvalidOperationException))]
    [InlineData(Refusal.SourceInAnotherTeam, typeof(KeyNotFoundException))]
    [InlineData(Refusal.TargetInAnotherTeam, typeof(KeyNotFoundException))]
    [InlineData(Refusal.NoSuchTarget, typeof(KeyNotFoundException))]
    [InlineData(Refusal.TargetRetired, typeof(InvalidOperationException))]
    public async Task A_relink_the_move_cannot_accept_is_refused_and_moves_nothing(Refusal shape, Type expected)
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var hookId = await _kit.SeedConnectionWebhookAsync(world, dead, RepositoryWebhookRegistrationStatus.Registered);
        var (from, to) = await ShapeAsync(world, shape, dead);

        var refusal = await Record.ExceptionAsync(() => RelinkAsync(world, world.OwnerId, from, to));

        refusal.ShouldNotBeNull($"{shape}: refused");
        refusal.ShouldBeOfType(expected, $"{shape}: {refusal.Message}");
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead, $"{shape}: nothing moved");
        (await _kit.LoadConnectionWebhookAsync(hookId)).CredentialId.ShouldBe(dead, $"{shape}: nor did the hook");
    }

    [Fact]
    public async Task A_target_that_cannot_read_a_repository_refuses_the_whole_move_and_names_it()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-narrow");
        var reachable = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var hidden = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var hiddenPath = (await _kit.LoadRepositoryAsync(hidden)).FullPath;
        var hookId = await _kit.SeedConnectionWebhookAsync(world, dead, RepositoryWebhookRegistrationStatus.Registered);
        var provider = new ProbeRecorder(refuseFor: hiddenPath);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => RelinkAsync(world, world.OwnerId, dead, target, provider));

        refusal.Message.ShouldContain(hiddenPath, Case.Sensitive, "the refusal names what the target cannot read, so the person knows what to unbind or which credential to pick");
        refusal.Message.ShouldContain("Nothing was moved");
        (await _kit.LoadRepositoryAsync(reachable)).CredentialId.ShouldBe(dead, "the reachable repository did not move either — a relink never leaves the dead credential half-emptied");
        (await _kit.LoadRepositoryAsync(hidden)).CredentialId.ShouldBe(dead);
        (await _kit.LoadConnectionWebhookAsync(hookId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task The_target_is_asked_to_read_every_repository_it_will_take_as_itself()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target");
        var repositories = new[] { await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error), await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error) };
        var provider = new ProbeRecorder();

        await RelinkAsync(world, world.OwnerId, dead, target, provider);

        provider.Reads.Select(read => read.Credential).Distinct().ShouldBe(new[] { target }, "the lookup is made with the credential about to take the repositories, never the dead one");
        provider.Reads.Select(read => read.ExternalId).ShouldBe((await Task.WhenAll(repositories.Select(_kit.LoadRepositoryAsync))).Select(r => r.ExternalId), ignoreOrder: true);
    }

    public enum ProbeFailure { Unauthorized401, Forbidden403, NotFound404, InsufficientScope, OAuthGrantRefused, ServiceUnavailable503, ConnectionDropped, RateLimited }

    [Theory]
    [InlineData(ProbeFailure.Unauthorized401, true)]
    [InlineData(ProbeFailure.Forbidden403, true)]
    [InlineData(ProbeFailure.NotFound404, true)]
    [InlineData(ProbeFailure.InsufficientScope, true)]
    [InlineData(ProbeFailure.OAuthGrantRefused, true)]
    [InlineData(ProbeFailure.ServiceUnavailable503, false)]
    [InlineData(ProbeFailure.ConnectionDropped, false)]
    [InlineData(ProbeFailure.RateLimited, false)]
    public async Task A_target_that_cannot_read_a_repository_is_told_apart_from_a_provider_that_could_not_answer(ProbeFailure failure, bool targetCannotRead)
    {
        // Only a provider that ANSWERED — this token is not let in, or cannot see that project — says anything about the
        // target. An outage, a dropped connection or a rate limit says nothing, and "pick a credential that reaches them" is
        // the wrong advice for it: the refusal must say the check could not be made, and that trying again is the remedy.
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target");
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var hookId = await _kit.SeedConnectionWebhookAsync(world, dead, RepositoryWebhookRegistrationStatus.Registered);
        var provider = new ProbeRecorder(refuseFor: string.Empty, failure: () => ProviderFailure(failure));

        var refusal = await Record.ExceptionAsync(() => RelinkAsync(world, world.OwnerId, dead, target, provider));

        refusal.ShouldNotBeNull($"{failure}: refused");
        refusal.Message.ShouldContain("Nothing was moved");

        if (targetCannotRead)
        {
            refusal.ShouldBeOfType<InvalidOperationException>($"{failure}: {refusal.Message}");
            refusal.Message.ShouldContain("cannot read");
        }
        else
        {
            refusal.ShouldBeAssignableTo<IFailure>($"{failure}: {refusal.Message}").Kind.ShouldBe(FailureKind.Unavailable, "retrying later is what could change the answer");
            refusal.Message.ShouldNotContain("cannot read", Case.Insensitive, $"{failure}: nothing was learned about the target, so the refusal must not blame it");
            refusal.Message.ShouldContain("try again", Case.Insensitive);
        }

        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead, $"{failure}: nothing moved");
        (await _kit.LoadConnectionWebhookAsync(hookId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task A_provider_that_could_not_answer_ends_the_check_at_the_first_repository()
    {
        // An outage is the same answer for every repository: asking the other ninety-nine would only hold the request, and
        // its database transaction, open for as long as the provider takes to say it again.
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target");

        for (var i = 0; i < 4; i++) await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        var provider = new ProbeRecorder(refuseFor: string.Empty, failure: () => ProviderFailure(ProbeFailure.ServiceUnavailable503));

        await Should.ThrowAsync<Exception>(() => RelinkAsync(world, world.OwnerId, dead, target, provider));

        provider.Reads.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_relink_of_more_repositories_than_one_action_verifies_is_refused_before_any_is_read()
    {
        // One provider call per repository, in sequence, inside the request's database transaction: unbounded, that is a request
        // that outlasts the provider's patience and the client's. Past the bound the move is refused and says what to do.
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target");
        var hookId = await _kit.SeedConnectionWebhookAsync(world, dead, RepositoryWebhookRegistrationStatus.Registered);
        var repositories = new List<Guid>();

        for (var i = 0; i <= CredentialRelinkService.MaxRepositoriesVerified; i++) repositories.Add(await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error));

        var provider = new ProbeRecorder();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => RelinkAsync(world, world.OwnerId, dead, target, provider));

        refusal.Message.ShouldContain($"at most {CredentialRelinkService.MaxRepositoriesVerified}");
        refusal.Message.ShouldContain("Nothing was moved");
        provider.Reads.ShouldBeEmpty("the bound is checked before the first call, not after a hundred of them");
        (await _kit.LoadRepositoryAsync(repositories[0])).CredentialId.ShouldBe(dead);
        (await _kit.LoadConnectionWebhookAsync(hookId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task A_relink_of_exactly_the_bound_is_verified_and_moves()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target");

        for (var i = 0; i < CredentialRelinkService.MaxRepositoriesVerified; i++) await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        var provider = new ProbeRecorder();

        var result = await RelinkAsync(world, world.OwnerId, dead, target, provider);

        result.MovedRepositoryCount.ShouldBe(CredentialRelinkService.MaxRepositoriesVerified);
        provider.Reads.Count.ShouldBe(CredentialRelinkService.MaxRepositoriesVerified, "each one asked, as the target");
    }

    [Fact]
    public async Task The_service_checks_credentials_manage_by_itself_not_only_through_the_commands_marker()
    {
        // The command's marker holds repos.manage; credentials.manage is asserted in the service because the credential marker
        // that would carry it refuses a retired credential. Both are Admin-tier today, so no role holds one and not the other and
        // the role matrix above cannot tell the two checks apart. Asked of the service directly, as a role below both, only the
        // service's own check can refuse.
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-members-own");
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        using var scope = _kit.Scope(world, world.MemberId);

        var refusal = await Should.ThrowAsync<TenantAccessDeniedException>(() => scope.Resolve<ICredentialRelinkService>().RelinkAllAsync(dead, target, CancellationToken.None));

        refusal.Reason.ShouldContain(TeamPermissions.CredentialsManage);
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    [Theory]
    [InlineData(TeamRole.Viewer, false)]
    [InlineData(TeamRole.Member, false)]
    [InlineData(TeamRole.Admin, true)]
    [InlineData(TeamRole.Owner, true)]
    public async Task Relinking_takes_repos_manage_and_credentials_manage_so_it_is_an_admins(TeamRole role, bool allowed)
    {
        var world = await _kit.SeedWorldAsync();
        var caller = await _kit.SeedMemberAsync(world, role);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, caller, "glpat-callers");
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        var outcome = await Record.ExceptionAsync(() => RelinkAsync(world, caller, dead, target));

        if (allowed)
        {
            outcome.ShouldBeNull($"{role}: {outcome?.Message}");
            (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(target);
            return;
        }

        outcome.ShouldBeOfType<TenantAccessDeniedException>($"{role}: below Admin");
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task Someone_outside_the_team_and_an_admin_of_another_team_relink_nothing()
    {
        var world = await _kit.SeedWorldAsync();
        var elsewhere = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(elsewhere, elsewhere.OwnerId, "glpat-their-own");
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        using var scope = _kit.Fixture.BeginScopeAs(elsewhere.OwnerId, world.TeamId);

        await Should.ThrowAsync<TenantAccessDeniedException>(() => scope.Resolve<IMediator>().Send(new RelinkCredentialBindingsCommand { FromCredentialId = dead, ToCredentialId = target }));
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task An_instance_admin_relinks_in_a_team_they_hold_no_membership_in()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var instanceAdmin = await _kit.SeedMemberAsync(world, TeamRole.Viewer);
        var target = await _kit.SeedCredentialAsync(world, instanceAdmin, "glpat-instance-admin");
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        using var scope = _kit.Scope(world, instanceAdmin, b => b.RegisterInstance(new TestCurrentUser(instanceAdmin, "instance-admin", Roles.Admin)).As<ICurrentUser>().SingleInstance());
        await scope.Resolve<IMediator>().Send(new RelinkCredentialBindingsCommand { FromCredentialId = dead, ToCredentialId = target });

        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(target, "the instance Admin stands as an Owner in every team, whatever role a membership row gives them");
    }

    [Fact]
    public async Task Suggestions_offer_the_callers_own_live_connections_first_then_team_tokens_and_never_a_teammates()
    {
        var world = await _kit.SeedWorldAsync();
        var revoked = DateTimeOffset.UtcNow.AddDays(-10);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked, createdDate: revoked);

        var before = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin-before", createdDate: revoked.AddDays(-3));
        var after = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin-after", createdDate: revoked.AddDays(2));
        var olderTeam = await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team-older", ownership: CredentialOwnership.TeamService, createdDate: revoked.AddDays(-5));
        var newerTeam = await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team-newer", ownership: CredentialOwnership.TeamService, createdDate: revoked.AddDays(5));

        var ownersPersonal = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner");
        var revokedOwn = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin-revoked", CredentialStatus.Revoked);
        var otherInstance = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin-elsewhere", instanceId: await _kit.SeedOtherInstanceAsync(world));
        var retired = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin-retired");
        await SoftDeleteCredentialAsync(retired);

        var suggestions = await SuggestAsync(world, world.AdminId, dead);

        suggestions.Select(s => s.CredentialId).ShouldBe(new[] { after, before, newerTeam, olderTeam }, "own connections newest first, then the team's tokens newest first");
        suggestions.Select(s => s.Kind).ShouldBe(new[] { RelinkSuggestionKind.YourConnection, RelinkSuggestionKind.YourConnection, RelinkSuggestionKind.TeamServiceToken, RelinkSuggestionKind.TeamServiceToken });
        suggestions.Select(s => s.ConnectedBeforeDisconnect).ShouldBe(new[] { false, true, false, true }, "the connect-then-disconnect shape is flagged — a connection made before the dead one was disconnected");

        var offered = suggestions.Select(s => s.CredentialId).ToHashSet();

        offered.ShouldNotContain(ownersPersonal, "a teammate's personal token would make the repositories act as them");
        offered.ShouldNotContain(revokedOwn);
        offered.ShouldNotContain(otherInstance);
        offered.ShouldNotContain(retired);
        offered.ShouldNotContain(dead);
    }

    [Fact]
    public async Task Suggestions_for_a_live_credential_exclude_itself_and_flag_nothing_as_disconnected_first()
    {
        // The Disconnect dialog asks "where should these go?" about a credential that is still live.
        var world = await _kit.SeedWorldAsync();
        var live = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-live");
        var other = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-other");

        var suggestions = await SuggestAsync(world, world.AdminId, live);

        suggestions.Select(s => s.CredentialId).ShouldBe(new[] { other });
        suggestions[0].ConnectedBeforeDisconnect.ShouldBeFalse("nothing has been disconnected yet");
    }

    public enum Asker { TheOwnerOfTheCredential, AnAdminForATeammate }

    public enum Candidate { TheOwnersOtherConnection, TheAskersOwnConnection, ATeamServiceToken, AnotherPersonsConnection, OnAnotherInstance, Revoked }

    [Theory]
    [InlineData(Asker.TheOwnerOfTheCredential, Candidate.TheOwnersOtherConnection, true)]
    [InlineData(Asker.TheOwnerOfTheCredential, Candidate.ATeamServiceToken, false)]
    [InlineData(Asker.TheOwnerOfTheCredential, Candidate.AnotherPersonsConnection, false)]
    [InlineData(Asker.TheOwnerOfTheCredential, Candidate.OnAnotherInstance, false)]
    [InlineData(Asker.TheOwnerOfTheCredential, Candidate.Revoked, false)]
    [InlineData(Asker.AnAdminForATeammate, Candidate.TheOwnersOtherConnection, true)]
    [InlineData(Asker.AnAdminForATeammate, Candidate.TheAskersOwnConnection, false)]
    [InlineData(Asker.AnAdminForATeammate, Candidate.ATeamServiceToken, false)]
    [InlineData(Asker.AnAdminForATeammate, Candidate.AnotherPersonsConnection, false)]
    public async Task Suggestions_for_a_live_credential_are_exactly_the_targets_a_disconnect_accepts(Asker asker, Candidate candidate, bool offered)
    {
        // The Disconnect dialog asks "where should these go?" about a credential that is still live, and a Disconnect that names
        // one of the answers must not refuse it — nor may the dialog omit the only target the Disconnect would accept. A live
        // credential's bindings may follow only to another connection of the SAME owner; the relink-all rule (the caller's own,
        // or the team's) is for a dead one.
        var world = await _kit.SeedWorldAsync();
        var (caller, owner) = asker == Asker.TheOwnerOfTheCredential ? (world.OwnerId, world.OwnerId) : (world.AdminId, world.MemberId);
        var source = await _kit.SeedCredentialAsync(world, owner, "glpat-source");
        var target = await SeedCandidateAsync(world, candidate, caller, owner);
        await _kit.SeedRepositoryAsync(world, source);

        var suggestions = await SuggestAsync(world, caller, source);
        var refusal = await Record.ExceptionAsync(() => _kit.DisconnectAsync(world, caller, source, moveBindingsTo: target));

        suggestions.Select(s => s.CredentialId).Contains(target).ShouldBe(offered, $"{asker} / {candidate}: offered");
        (refusal == null).ShouldBe(offered, $"{asker} / {candidate}: the Disconnect {(refusal == null ? "accepted" : "refused")} it — {refusal?.Message}");
    }

    [Fact]
    public async Task Suggestions_for_a_live_credential_leave_out_a_connection_whose_holder_no_longer_stands_in_the_team()
    {
        // The departed-token sweep retires a personal credential whose person has no standing in the team, so a Disconnect
        // refuses to move repositories onto one — and the dialog must not offer what the Disconnect would refuse.
        var world = await _kit.SeedWorldAsync();
        var stranger = await SeedStrangerAsync();
        var source = await _kit.SeedCredentialAsync(world, stranger, "glpat-stranger-a");
        var other = await _kit.SeedCredentialAsync(world, stranger, "glpat-stranger-b");
        await _kit.SeedRepositoryAsync(world, source);

        var suggestions = await SuggestAsync(world, world.AdminId, source);
        var refusal = await Record.ExceptionAsync(() => _kit.DisconnectAsync(world, world.AdminId, source, moveBindingsTo: other));

        suggestions.ShouldBeEmpty();
        refusal.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("no longer has standing");
    }

    private async Task<Guid> SeedCandidateAsync(BindingMoveKit.World world, Candidate candidate, Guid asker, Guid owner)
    {
        switch (candidate)
        {
            case Candidate.TheOwnersOtherConnection: return await _kit.SeedCredentialAsync(world, owner, "glpat-owners-other");
            case Candidate.TheAskersOwnConnection: return await _kit.SeedCredentialAsync(world, asker, "glpat-askers");
            case Candidate.ATeamServiceToken: return await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team", ownership: CredentialOwnership.TeamService);
            case Candidate.AnotherPersonsConnection: return await _kit.SeedCredentialAsync(world, world.ViewerId, "glpat-viewers");
            case Candidate.OnAnotherInstance: return await _kit.SeedCredentialAsync(world, owner, "glpat-elsewhere", instanceId: await _kit.SeedOtherInstanceAsync(world));
            case Candidate.Revoked: return await _kit.SeedCredentialAsync(world, owner, "glpat-gone", CredentialStatus.Revoked);
            default: throw new ArgumentOutOfRangeException(nameof(candidate), candidate, "a shape the test does not seed");
        }
    }

    [Fact]
    public async Task Suggestions_for_a_credential_of_another_team_read_as_not_found()
    {
        var world = await _kit.SeedWorldAsync();
        var elsewhere = await _kit.SeedWorldAsync();
        var foreign = await _kit.SeedCredentialAsync(elsewhere, elsewhere.OwnerId, "glpat-foreign");

        await Should.ThrowAsync<KeyNotFoundException>(() => SuggestAsync(world, world.AdminId, foreign));
        await Should.ThrowAsync<KeyNotFoundException>(() => DependentsAsync(world, world.AdminId, foreign));
    }

    [Fact]
    public async Task Dependents_list_what_leans_on_a_retired_credential_with_the_projects_and_triggers_that_name_it()
    {
        var world = await _kit.SeedWorldAsync();
        var leaver = await _kit.SeedMemberAsync(world, TeamRole.Member);
        var theirs = await _kit.SeedCredentialAsync(world, leaver, "glpat-leaver");
        var repoId = await _kit.SeedRepositoryAsync(world, theirs);
        var quiet = await _kit.SeedRepositoryAsync(world, theirs);
        var elsewhere = await _kit.SeedRepositoryAsync(world, await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner"));
        var hookId = await _kit.SeedConnectionWebhookAsync(world, theirs, RepositoryWebhookRegistrationStatus.Registered);
        await _kit.SeedConnectionWebhookAsync(world, theirs, RepositoryWebhookRegistrationStatus.Cancelled);
        var identityId = await SeedIdentityAsync(world, leaver, theirs);
        var (alpha, beta) = (await SeedProjectAsync(world, "Alpha"), await SeedProjectAsync(world, "Beta"));
        await LinkProjectAsync(world, alpha, repoId);
        await LinkProjectAsync(world, beta, repoId);
        var triggering = await SeedActivationAsync(world, "Review on push", new { repositories = new[] { new { repositoryId = repoId.ToString() } } });
        await SeedActivationAsync(world, "Names someone else's", new { repositoryId = elsewhere.ToString() });
        await SeedActivationAsync(world, "Names nothing", new { });

        await RemoveFromTeamAsync(world, leaver);

        var dependents = await DependentsAsync(world, world.ViewerId, theirs);

        dependents.Retired.ShouldBeTrue("a departed member's token is soft-deleted, and the team can still ask what is left on it");
        dependents.Status.ShouldBe(CredentialStatus.Revoked);
        dependents.Repositories.Select(r => r.Id).ShouldBe(new[] { repoId, quiet }, ignoreOrder: true);
        dependents.Repositories.Single(r => r.Id == repoId).Projects.Select(p => p.Name).ShouldBe(new[] { "Alpha", "Beta" });
        dependents.Repositories.Single(r => r.Id == quiet).Projects.ShouldBeEmpty();
        dependents.Repositories.ShouldAllBe(r => r.Status == RepositoryStatus.Error && r.LastError == CredentialRevocationService.DisconnectedRepositoryError);
        dependents.ConnectionWebhooks.Select(h => h.Id).ShouldBe(new[] { hookId }, "a retired hook is not waiting for anything");
        dependents.Identities.ShouldBeEmpty("departing soft-deleted the act-as identity with the token");
        dependents.Activations.Select(a => a.ActivationId).ShouldBe(new[] { triggering });
        dependents.Activations[0].RepositoryIds.ShouldBe(new[] { repoId });
        dependents.Activations[0].WorkflowName.ShouldBe("Review on push");
        identityId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Dependents_leave_out_what_is_removed_and_what_belongs_to_another_team()
    {
        // Each of these names a repository bound through the credential and none of them is waiting on it: a removed repository,
        // a removed project, a removed project link, a removed trigger, a trigger of a removed workflow, and — the one that
        // would be a leak, not a miscount — another team's trigger that names this team's repository id.
        var world = await _kit.SeedWorldAsync();
        var elsewhere = await _kit.SeedWorldAsync();
        var theirs = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-theirs", CredentialStatus.Revoked);
        var live = await _kit.SeedRepositoryAsync(world, theirs, RepositoryStatus.Error);
        var removed = await _kit.SeedRepositoryAsync(world, theirs, RepositoryStatus.Error);
        await SoftDeleteRepositoryAsync(removed);

        var kept = await SeedProjectAsync(world, "Kept");
        var removedProject = await SeedProjectAsync(world, "RemovedProject");
        var removedLink = await SeedProjectAsync(world, "RemovedLink");
        await LinkProjectAsync(world, kept, live);
        await LinkProjectAsync(world, removedProject, live);
        await LinkProjectAsync(world, removedLink, live);
        await SoftDeleteProjectAsync(removedProject);
        await SoftDeleteProjectLinkAsync(removedLink, live);

        var naming = new { repositories = new[] { new { repositoryId = live.ToString() } } };
        var listed = await SeedActivationAsync(world, "Listed", naming);
        await SoftDeleteActivationAsync(await SeedActivationAsync(world, "Removed trigger", naming));
        var underRemovedWorkflow = await SeedActivationAsync(world, "Removed workflow", naming);
        await SoftDeleteWorkflowAsync(await WorkflowOfAsync(underRemovedWorkflow));
        await SeedActivationAsync(elsewhere, "Another team's", naming);

        var dependents = await DependentsAsync(world, world.AdminId, theirs);

        dependents.Repositories.Select(r => r.Id).ShouldBe(new[] { live }, "a removed repository is not waiting for anything");
        dependents.Repositories[0].Projects.Select(p => p.Name).ShouldBe(new[] { "Kept" }, "a removed project, and a project link that was removed, are not the repository's projects");
        dependents.Activations.Select(a => a.ActivationId).ShouldBe(new[] { listed }, "only this team's live triggers of live workflows");
    }

    [Fact]
    public async Task Dependents_of_a_live_credential_include_the_identity_a_disconnect_would_end()
    {
        var world = await _kit.SeedWorldAsync();
        var live = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-live");
        var identityId = await SeedIdentityAsync(world, world.AdminId, live);

        var dependents = await DependentsAsync(world, world.AdminId, live);

        dependents.Retired.ShouldBeFalse();
        dependents.Identities.Select(i => i.Id).ShouldBe(new[] { identityId });
        dependents.Identities[0].UserId.ShouldBe(world.AdminId);
        dependents.Identities[0].UserName.ShouldNotBeNullOrEmpty();
    }

    /// <summary>What a provider throws, as the resilience layer hands it to a caller, for each way a lookup can fail.</summary>
    private static Exception ProviderFailure(ProbeFailure failure) => failure switch
    {
        ProbeFailure.Unauthorized401 => new ProviderApiException(ProviderKind.GitLab, 401, "GetByExternalIdAsync", "401 Unauthorized", new InvalidOperationException("token rejected")),
        ProbeFailure.Forbidden403 => new ProviderApiException(ProviderKind.GitLab, 403, "GetByExternalIdAsync", "403 Forbidden", new InvalidOperationException("not a member")),
        ProbeFailure.NotFound404 => new ProviderApiException(ProviderKind.GitLab, 404, "GetByExternalIdAsync", "404 Project Not Found", new InvalidOperationException("not visible to this token")),
        ProbeFailure.InsufficientScope => new ProviderInsufficientScopeException(ProviderKind.GitLab, nameof(IRepositoryCatalogCapability), new[] { "read_api" }, new[] { "read_user" }),
        ProbeFailure.OAuthGrantRefused => new OAuthExchangeException("invalid_grant", "the refresh token was revoked", null),
        ProbeFailure.ServiceUnavailable503 => new ProviderApiException(ProviderKind.GitLab, 503, "GetByExternalIdAsync", "503 Service Unavailable", new InvalidOperationException("upstream down")),
        ProbeFailure.ConnectionDropped => new HttpRequestException("connection reset by peer"),
        ProbeFailure.RateLimited => new CodeSpace.Core.Services.Providers.Resilience.ProviderRateLimitedException(Guid.NewGuid(), "GetByExternalIdAsync"),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "a failure the test does not throw")
    };

    // ── Doors ─────────────────────────────────────────────────────────────────────

    private async Task<RelinkCredentialBindingsResult> RelinkAsync(BindingMoveKit.World world, Guid userId, Guid from, Guid to, ProbeRecorder? provider = null)
    {
        using var scope = _kit.Scope(world, userId, provider == null ? null : provider.Install);

        return await scope.Resolve<IMediator>().Send(new RelinkCredentialBindingsCommand { FromCredentialId = from, ToCredentialId = to });
    }

    private async Task<IReadOnlyList<RelinkSuggestion>> SuggestAsync(BindingMoveKit.World world, Guid userId, Guid credentialId)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new GetRelinkSuggestionsQuery { CredentialId = credentialId });
    }

    private async Task<CredentialDependents> DependentsAsync(BindingMoveKit.World world, Guid userId, Guid credentialId)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new GetCredentialDependentsQuery { CredentialId = credentialId });
    }

    private async Task RemoveFromTeamAsync(BindingMoveKit.World world, Guid userId)
    {
        using var scope = _kit.Scope(world, world.OwnerId);

        await scope.Resolve<IMediator>().Send(new RemoveTeamMemberCommand { UserId = userId });
    }

    // ── Shapes ────────────────────────────────────────────────────────────────────

    private async Task<(Guid From, Guid To)> ShapeAsync(BindingMoveKit.World world, Refusal shape, Guid dead)
    {
        var good = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-good");

        switch (shape)
        {
            case Refusal.SourceStillConnected: return (await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-live-source"), good);
            case Refusal.TargetIsAnotherPersonsConnection: return (dead, await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-teammates"));
            case Refusal.TargetOnAnotherInstance: return (dead, await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-elsewhere", instanceId: await _kit.SeedOtherInstanceAsync(world)));
            case Refusal.TargetRevoked: return (dead, await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-gone", CredentialStatus.Revoked));
            case Refusal.TargetIsTheSource: return (dead, dead);
            case Refusal.SourceInAnotherTeam: return (await ForeignCredentialAsync(), good);
            case Refusal.TargetInAnotherTeam: return (dead, await ForeignCredentialAsync());
            case Refusal.NoSuchTarget: return (dead, Guid.NewGuid());
            case Refusal.TargetRetired: return (dead, await RetiredAsync(world));
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, "a shape the test does not seed");
        }
    }

    private async Task<Guid> ForeignCredentialAsync()
    {
        var elsewhere = await _kit.SeedWorldAsync();

        return await _kit.SeedCredentialAsync(elsewhere, elsewhere.OwnerId, "glpat-foreign");
    }

    private async Task<Guid> RetiredAsync(BindingMoveKit.World world)
    {
        var retired = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-retired");
        await SoftDeleteCredentialAsync(retired);

        return retired;
    }

    // ── Seeds ─────────────────────────────────────────────────────────────────────

    /// <summary>A person with an account and no membership in the team — what a departed member is, before the sweep retires what they lent.</summary>
    private async Task<Guid> SeedStrangerAsync()
    {
        var stranger = new User { Id = Guid.NewGuid(), Email = $"stranger-{Guid.NewGuid():N}@x", Name = "stranger" };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.User.Add(stranger);
        await db.SaveChangesAsync();

        return stranger.Id;
    }

    private async Task SoftDeleteCredentialAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Credential.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task SoftDeleteRepositoryAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task SoftDeleteProjectAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Project.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task SoftDeleteProjectLinkAsync(Guid projectId, Guid repositoryId)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().ProjectRepository.Where(l => l.ProjectId == projectId && l.RepositoryId == repositoryId).ExecuteUpdateAsync(s => s.SetProperty(l => l.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task SoftDeleteActivationAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().WorkflowActivation.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task<Guid> WorkflowOfAsync(Guid activationId)
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowActivation.AsNoTracking().Where(a => a.Id == activationId).Select(a => a.WorkflowId).SingleAsync();
    }

    private async Task SoftDeleteWorkflowAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Workflow.Where(w => w.Id == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task<Guid> SeedIdentityAsync(BindingMoveKit.World world, Guid userId, Guid credentialId)
    {
        var identity = new UserProviderIdentity { Id = Guid.NewGuid(), UserId = userId, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, ProviderUserId = $"gl-{Guid.NewGuid():N}"[..10], ProviderUsername = $"gl-user-{Guid.NewGuid():N}"[..14] };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync();

        return identity.Id;
    }

    private async Task<Guid> SeedProjectAsync(BindingMoveKit.World world, string name)
    {
        var project = new Project { Id = Guid.NewGuid(), TeamId = world.TeamId, Name = name, Slug = $"{name.ToLowerInvariant()}-{Guid.NewGuid():N}"[..14] };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Project.Add(project);
        await db.SaveChangesAsync();

        return project.Id;
    }

    private async Task LinkProjectAsync(BindingMoveKit.World world, Guid projectId, Guid repositoryId)
    {
        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ProjectRepository.Add(new ProjectRepository { ProjectId = projectId, RepositoryId = repositoryId, TeamId = world.TeamId });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedActivationAsync(BindingMoveKit.World world, string workflowName, object config)
    {
        var workflow = new Workflow { Id = Guid.NewGuid(), TeamId = world.TeamId, Slug = $"wf-{Guid.NewGuid():N}"[..14], Name = workflowName, DefinitionJson = "{}" };
        var activation = new WorkflowActivation { Id = Guid.NewGuid(), WorkflowId = workflow.Id, TypeKey = "trigger.pull_request", ConfigJson = JsonSerializer.Serialize(config), Enabled = true };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Workflow.Add(workflow);
        db.WorkflowActivation.Add(activation);
        await db.SaveChangesAsync();

        return activation.Id;
    }

    /// <summary>
    /// The provider's catalog, answering as the Git test provider does but remembering who it was asked as — and, for one
    /// repository path, refusing the way a provider answers a token that cannot see a project. A dispatch proxy rather than a
    /// class implementing the capability: the fixture registers every capability implementation in this assembly it finds.
    /// </summary>
    public sealed class ProbeRecorder
    {
        private readonly string? _refuseFor;
        private readonly Func<Exception>? _failure;
        private readonly List<(Guid Credential, string ExternalId)> _reads = new();

        /// <param name="refuseFor">Refuse the repositories whose external id ends with it; the empty string refuses them all, null none.</param>
        /// <param name="failure">What the provider throws when it refuses; a 404 when left out.</param>
        public ProbeRecorder(string? refuseFor = null, Func<Exception>? failure = null)
        {
            _refuseFor = refuseFor;
            _failure = failure;
        }

        public IReadOnlyList<(Guid Credential, string ExternalId)> Reads => _reads;

        public void Install(ContainerBuilder builder) =>
            builder.Register<IProviderRegistry>(c => new RecordingRegistry(new BindingMoveKit.GitTestProviderRegistry(new ProviderRegistry(c.Resolve<IEnumerable<IProviderCapability>>())), this)).InstancePerLifetimeScope();

        private void Read(ProviderContext context, string externalId)
        {
            _reads.Add((context.Credential.Id, externalId));

            if (_refuseFor != null && externalId.EndsWith(_refuseFor, StringComparison.Ordinal))
                throw _failure?.Invoke() ?? new ProviderApiException(ProviderKind.GitLab, 404, "GetByExternalIdAsync", "404 Project Not Found", new InvalidOperationException("not visible to this token"));
        }

        private sealed class RecordingRegistry : IProviderRegistry
        {
            private readonly IProviderRegistry _inner;
            private readonly ProbeRecorder _recorder;

            public RecordingRegistry(IProviderRegistry inner, ProbeRecorder recorder)
            {
                _inner = inner;
                _recorder = recorder;
            }

            public TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability =>
                typeof(TCapability) == typeof(IRepositoryCatalogCapability) ? (TCapability)CatalogProxy.Create(_inner.Require<IRepositoryCatalogCapability>(kind), _recorder) : _inner.Require<TCapability>(kind);

            public bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability => _inner.TryGet(kind, out capability);

            public IReadOnlyList<Type> GetCapabilities(ProviderKind kind) => _inner.GetCapabilities(kind);
        }

        public class CatalogProxy : System.Reflection.DispatchProxy
        {
            private IRepositoryCatalogCapability _inner = default!;
            private ProbeRecorder _recorder = default!;

            public static object Create(IRepositoryCatalogCapability inner, ProbeRecorder recorder)
            {
                var proxy = Create<IRepositoryCatalogCapability, CatalogProxy>();
                var self = (CatalogProxy)(object)proxy;

                self._inner = inner;
                self._recorder = recorder;

                return proxy;
            }

            protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod!.Name == nameof(IRepositoryCatalogCapability.GetByExternalIdAsync))
                {
                    try { _recorder.Read((ProviderContext)args![0]!, (string)args[1]!); }
                    catch (Exception refusal) { return Task.FromException<RemoteRepository>(refusal); }
                }

                try { return targetMethod.Invoke(_inner, args); }
                catch (System.Reflection.TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
            }
        }
    }
}
