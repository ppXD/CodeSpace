using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>The doors around a move: the dialog's candidate list, the Disconnect that names a target, a connect's carry-forward and the repair sweep, each across two connections to one host.</summary>
public sealed partial class SameHostConnectionsFlowTests
{
    [Fact]
    public async Task Candidates_are_the_callers_own_and_the_teams_live_credentials_on_every_connection_to_the_host_best_first()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var otherHost = await _kit.SeedOtherInstanceAsync(world);
        var now = DateTimeOffset.UtcNow;
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked, createdDate: now.AddDays(-20));

        var ownHere = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-own-here", createdDate: now.AddDays(-5));
        var teamHere = await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team-here", ownership: CredentialOwnership.TeamService, createdDate: now.AddDays(-1));
        var ownThere = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-own-there", instanceId: sibling, createdDate: now);
        var teamThere = await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team-there", ownership: CredentialOwnership.TeamService, instanceId: sibling, createdDate: now.AddDays(-3));

        var teammates = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-teammates", instanceId: sibling);
        var anotherHost = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-another-host", instanceId: otherHost);
        var revoked = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-revoked", CredentialStatus.Revoked, instanceId: sibling);
        var retired = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-retired", instanceId: sibling);
        await SoftDeleteCredentialAsync(retired);

        var candidates = await CandidatesAsync(world, world.AdminId, world.InstanceId, dead);

        candidates.Select(c => c.CredentialId).ShouldBe(new[] { ownHere, teamHere, ownThere, teamThere }, "the repository's own connection first — nothing about where it lives changes — then the caller's own before the team's, each newest first");
        candidates.Select(c => c.Kind).ShouldBe(new[] { RelinkSuggestionKind.YourConnection, RelinkSuggestionKind.TeamServiceToken, RelinkSuggestionKind.YourConnection, RelinkSuggestionKind.TeamServiceToken });
        candidates.Select(c => c.ProviderInstanceId).ShouldBe(new[] { world.InstanceId, world.InstanceId, sibling, sibling });
        candidates.ShouldAllBe(c => c.ProviderInstanceName.StartsWith("GitLab"), "a person can tell two connections to one host apart by name");

        var offered = candidates.Select(c => c.CredentialId).ToHashSet();

        offered.ShouldNotContain(teammates, "a teammate's personal token would make the repository act as them");
        offered.ShouldNotContain(anotherHost);
        offered.ShouldNotContain(revoked);
        offered.ShouldNotContain(retired);
        offered.ShouldNotContain(dead);
    }

    [Fact]
    public async Task Candidates_reach_across_the_connections_whichever_one_the_dialog_was_opened_from()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var now = DateTimeOffset.UtcNow;
        var here = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-here", createdDate: now);
        var there = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-there", instanceId: sibling, createdDate: now.AddDays(-1));

        (await CandidatesAsync(world, world.AdminId, world.InstanceId)).Select(c => c.CredentialId).ShouldBe(new[] { here, there });
        (await CandidatesAsync(world, world.AdminId, sibling)).Select(c => c.CredentialId).ShouldBe(new[] { there, here }, "opened from the sibling, the sibling's credentials lead");
    }

    [Fact]
    public async Task Candidates_leave_out_the_credential_being_left_and_flag_the_one_connected_before_it_was_disconnected()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var revoked = DateTimeOffset.UtcNow.AddDays(-10);
        var dead = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-dead", CredentialStatus.Revoked, createdDate: revoked);
        var before = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-before", instanceId: sibling, createdDate: revoked.AddDays(-3));
        var after = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-after", instanceId: sibling, createdDate: revoked.AddDays(2));

        var candidates = await CandidatesAsync(world, world.AdminId, world.InstanceId, dead);

        candidates.Select(c => (c.CredentialId, c.ConnectedBeforeDisconnect)).ShouldBe(new[] { (after, false), (before, true) });
    }

    [Fact]
    public async Task Candidates_do_not_depend_on_the_credential_being_left_being_dead_or_on_there_being_one()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var rejecting = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-rejecting");
        var other = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-other", instanceId: sibling);

        var whileLive = await CandidatesAsync(world, world.AdminId, world.InstanceId, rejecting);
        var unbound = await CandidatesAsync(world, world.AdminId, world.InstanceId);

        whileLive.Select(c => c.CredentialId).ShouldBe(new[] { other }, "a repository whose credential merely looks fine is moved off it as readily as a dead one's");
        unbound.Select(c => c.CredentialId).ShouldBe(new[] { rejecting, other }, "and a repository with no credential at all is offered every one");
    }

    [Fact]
    public async Task Candidates_leave_out_a_static_token_past_its_expiry_exactly_as_the_credential_list_reads_it()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var now = DateTimeOffset.UtcNow;

        // Newest first would put every expired one at the top, so each is the freshest of its kind: if one were offered, it would lead.
        var expiredHere = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-expired-here", createdDate: now, expiresDate: now.AddDays(-3));
        var expiredTeam = await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-expired-team", ownership: CredentialOwnership.TeamService, createdDate: now, expiresDate: now.AddHours(-1));
        var expiredThere = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-expired-there", instanceId: sibling, createdDate: now, expiresDate: now.AddMinutes(-5));
        var expiring = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-expiring", createdDate: now.AddDays(-1), expiresDate: now.AddDays(5));
        var healthy = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-healthy", createdDate: now.AddDays(-2), expiresDate: now.AddDays(200));
        var noExpiry = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-no-expiry", createdDate: now.AddDays(-3));

        var candidates = await CandidatesAsync(world, world.AdminId, world.InstanceId);
        var listed = await ListCredentialsAsync(world, world.AdminId);

        candidates.Select(c => c.CredentialId).ShouldBe(new[] { expiring, healthy, noExpiry }, "an expired token is Active by status until a probe flips it, and is still not one a repository can be re-linked to");

        foreach (var credential in listed.Where(c => new[] { expiredHere, expiredTeam, expiredThere, expiring, healthy, noExpiry }.Contains(c.Id)))
        {
            var offered = candidates.Any(c => c.CredentialId == credential.Id);
            var canAuthenticate = credential.Health is CredentialHealth.Active or CredentialHealth.Expiring;

            offered.ShouldBe(canAuthenticate, $"{credential.DisplayName}: listed as {credential.Health}, offered {offered} — the list and the dialog read one rule");
        }
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_candidate_on_another_connection_names_the_teammates_whose_act_as_the_move_strands_and_the_act_as_reader_agrees(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-target", instanceId: sibling);
        var sameConnection = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-same-connection", createdDate: DateTimeOffset.UtcNow.AddDays(-9));

        // The member acts as themselves on the repository's own connection only; the owner on both, so a move strands nothing of theirs; the viewer has no identity at all.
        await SeedIdentityAsync(world.MemberId, world.InstanceId, await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-here"));
        await SeedIdentityAsync(world.OwnerId, world.InstanceId, await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner-here"));
        await SeedIdentityAsync(world.OwnerId, sibling, await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner-there", instanceId: sibling));
        var memberName = await NameOfAsync(world.MemberId);

        var candidates = await CandidatesAsync(world, world.AdminId, world.InstanceId, dead);

        candidates.Single(c => c.CredentialId == target).StrandedActAs.ShouldBe(new[] { memberName }, "the member can act as themselves here and has no identity on the connection the repository would move to");
        candidates.Single(c => c.CredentialId == sameConnection).StrandedActAs.ShouldBeEmpty("a credential on the repository's own connection moves nothing, so it strands nobody");

        // The act-as reader says the same, before and after: the people named are exactly those it can no longer resolve once the repository lives on the sibling.
        (await ResolveActAsAsync(world.MemberId, world.InstanceId)).ShouldNotBeNull();
        (await ResolveActAsAsync(world.MemberId, sibling)).ShouldBeNull();
        (await ActAsAuthorsAsync(world, repoId)).ShouldBe(new[] { world.MemberId, world.OwnerId }, "before the move both can be an author (the picker lists them by name)");

        await MoveAsync(door, world, world.AdminId, dead, repoId, target);

        var moved = await _kit.LoadRepositoryAsync(repoId);

        moved.ProviderInstanceId.ShouldBe(sibling);
        (await ResolveActAsAsync(world.MemberId, moved.ProviderInstanceId)).ShouldBeNull("the member's identity belongs to the connection the repository left, so acting as them on it now asks them to connect again");
        (await ResolveActAsAsync(world.OwnerId, moved.ProviderInstanceId)).ShouldNotBeNull("the owner had an identity on both");
        (await ActAsAuthorsAsync(world, repoId)).ShouldBe(new[] { world.OwnerId }, "the author picker offers what resolves, which is no longer the member");
        (await ResolveActAsAsync(world.MemberId, world.InstanceId)).ShouldNotBeNull("nothing was taken from the member: their identity is intact on the connection it belongs to");

        await SeedIdentityAsync(world.MemberId, sibling, await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-there", instanceId: sibling));

        (await ResolveActAsAsync(world.MemberId, moved.ProviderInstanceId)).ShouldNotBeNull("once they connect on the new connection they act as themselves on the repository again — what the dialog told them");
    }

    [Fact]
    public async Task A_candidate_strands_nobody_when_nobody_acts_as_themselves_on_the_repositorys_connection_or_every_one_who_does_has_an_identity_on_the_candidates()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var target = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-target", instanceId: sibling);

        (await CandidatesAsync(world, world.AdminId, world.InstanceId)).Single().StrandedActAs.ShouldBeEmpty("no identity on the repository's connection: nothing to strand");

        await SeedIdentityAsync(world.MemberId, world.InstanceId, await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-here"));
        await SeedIdentityAsync(world.MemberId, sibling, await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-there", instanceId: sibling));

        (await CandidatesAsync(world, world.AdminId, world.InstanceId)).Single(c => c.CredentialId == target).StrandedActAs.ShouldBeEmpty("the member already has an identity on the candidate's connection");
    }

    [Fact]
    public async Task Candidates_for_another_teams_connection_read_as_not_found()
    {
        var world = await _kit.SeedWorldAsync();
        var elsewhere = await _kit.SeedWorldAsync();

        var refusal = await Record.ExceptionAsync(() => CandidatesAsync(world, world.AdminId, elsewhere.InstanceId));

        refusal.ShouldBeOfType<KeyNotFoundException>(refusal?.Message);
    }

    public enum Disconnecting { TheOwnersOtherConnectionOnASibling, TheAskersOwnConnectionOnASibling, ATeamTokenOnASibling }

    [Theory]
    [InlineData(Disconnecting.TheOwnersOtherConnectionOnASibling, true)]
    [InlineData(Disconnecting.TheAskersOwnConnectionOnASibling, false)]
    [InlineData(Disconnecting.ATeamTokenOnASibling, false)]
    public async Task Suggestions_for_a_live_credential_reach_a_sibling_exactly_as_far_as_a_disconnect_accepts(Disconnecting candidate, bool offered)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var source = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-source");
        var target = candidate switch
        {
            Disconnecting.TheOwnersOtherConnectionOnASibling => await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-owners-other", instanceId: sibling),
            Disconnecting.TheAskersOwnConnectionOnASibling => await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-askers", instanceId: sibling),
            _ => await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team", ownership: CredentialOwnership.TeamService, instanceId: sibling)
        };
        await _kit.SeedRepositoryAsync(world, source);

        var suggestions = await SuggestAsync(world, world.AdminId, source);
        var refusal = await Record.ExceptionAsync(() => _kit.DisconnectAsync(world, world.AdminId, source, moveBindingsTo: target));

        suggestions.Select(s => s.CredentialId).Contains(target).ShouldBe(offered, $"{candidate}: offered");
        (refusal == null).ShouldBe(offered, $"{candidate}: the Disconnect {(refusal == null ? "accepted" : "refused")} it — {refusal?.Message}");
    }

    [Fact]
    public async Task A_disconnect_that_names_a_credential_on_a_sibling_connection_takes_the_repository_there()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var leaving = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-leaving");
        var target = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-target", instanceId: sibling);
        var repoId = await _kit.SeedRepositoryAsync(world, leaving);

        var result = await _kit.DisconnectAsync(world, world.AdminId, leaving, moveBindingsTo: target);

        result.MovedRepositoryCount.ShouldBe(1);
        result.MovedToCredentialId.ShouldBe(target);
        var repo = await _kit.LoadRepositoryAsync(repoId);
        (repo.CredentialId, repo.ProviderInstanceId, repo.Status).ShouldBe((target, sibling, RepositoryStatus.Active));
        (await _kit.LoadCredentialAsync(leaving)).Status.ShouldBe(CredentialStatus.Revoked);
    }

    [Fact]
    public async Task A_disconnect_whose_named_target_cannot_take_a_repository_refuses_before_it_revokes_anything()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var leaving = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-leaving");
        var target = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-target", instanceId: sibling);
        var repoId = await _kit.SeedRepositoryAsync(world, leaving, name: "api");
        await _kit.SeedRepositoryAsync(world, target, instanceId: sibling, name: "api");

        var refusal = await Record.ExceptionAsync(() => _kit.DisconnectAsync(world, world.AdminId, leaving, moveBindingsTo: target));

        refusal.ShouldBeOfType<InvalidOperationException>(refusal?.Message);
        refusal.Message.ShouldContain("already bound under", Case.Sensitive);
        (await _kit.LoadCredentialAsync(leaving)).Status.ShouldBe(CredentialStatus.Active, "a refused target leaves the credential exactly as it was — the provider was never asked to revoke it");
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(leaving);
    }

    [Fact]
    public async Task A_disconnect_that_names_nobody_never_implies_a_sibling_connection()
    {
        // The implied destination is where reconnecting would have sent it, on this connection. A repository changing connection is a bigger step than
        // an owner who happens to hold a credential elsewhere has asked for, so it takes a named target.
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var leaving = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-leaving");
        await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-elsewhere", instanceId: sibling);
        var repoId = await _kit.SeedRepositoryAsync(world, leaving);

        var result = await _kit.DisconnectAsync(world, world.AdminId, leaving);

        result.MovedRepositoryCount.ShouldBe(0);
        var repo = await _kit.LoadRepositoryAsync(repoId);
        (repo.CredentialId, repo.Status).ShouldBe((leaving, RepositoryStatus.Error));
    }

    [Fact]
    public async Task Connecting_never_moves_what_another_person_bound_and_a_refused_repository_does_not_stop_the_rest()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var long_ago = DateTimeOffset.UtcNow.AddDays(-10);
        var theirs = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-members-dead", CredentialStatus.Revoked, createdDate: long_ago);
        var mine = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admins-dead", CredentialStatus.Revoked, createdDate: long_ago);
        var members = await _kit.SeedRepositoryAsync(world, theirs, RepositoryStatus.Error);
        var blocked = await _kit.SeedRepositoryAsync(world, mine, RepositoryStatus.Error, name: "blocked");
        var free = await _kit.SeedRepositoryAsync(world, mine, RepositoryStatus.Error, name: "free");
        var holder = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-holder", instanceId: sibling);
        await _kit.SeedRepositoryAsync(world, holder, instanceId: sibling, name: "blocked");

        var connected = await _kit.AddTokenAsync(world, world.AdminId, "glpat-new", instanceId: sibling);

        (await _kit.LoadRepositoryAsync(members)).CredentialId.ShouldBe(theirs, "a different person is a different identity: nothing they bound follows someone else's connect");
        (await _kit.LoadRepositoryAsync(free)).CredentialId.ShouldBe(connected, "the repository the connection can take goes");
        var left = await _kit.LoadRepositoryAsync(blocked);
        (left.CredentialId, left.ProviderInstanceId, left.Status).ShouldBe((mine, world.InstanceId, RepositoryStatus.Error), "the one the connection already holds a row for stays exactly where it was — the connect itself succeeds");
    }

    [Fact]
    public async Task The_sweep_prefers_the_owners_credential_on_the_dead_ones_own_connection()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var revoked = DateTimeOffset.UtcNow.AddDays(-10);
        var dead = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-dead", CredentialStatus.Revoked, createdDate: revoked);
        var onSibling = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-newer-on-the-sibling", instanceId: sibling, createdDate: revoked.AddDays(3));
        var here = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-older-here", createdDate: revoked.AddDays(1));
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        await SweepUntilAsync(async () => (await _kit.LoadRepositoryAsync(repoId)).CredentialId != dead);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        (repo.CredentialId, repo.ProviderInstanceId).ShouldBe((here, world.InstanceId), "the credential on the connection the repository already lives under — no change of connection when one is not needed");
        onSibling.ShouldNotBe(repo.CredentialId!.Value);
    }

    [Fact]
    public async Task The_sweep_never_follows_a_different_person_nor_a_credential_connected_before_the_dead_one_died()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var revoked = DateTimeOffset.UtcNow.AddDays(-10);
        var dead = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-dead", CredentialStatus.Revoked, createdDate: revoked);
        await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-another-person", instanceId: sibling, createdDate: revoked.AddDays(2));
        await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-before-it-died", instanceId: sibling, createdDate: revoked.AddDays(-2));
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var passes = 0;

        await SweepUntilAsync(() => Task.FromResult(++passes >= 3));

        var repo = await _kit.LoadRepositoryAsync(repoId);

        (repo.CredentialId, repo.ProviderInstanceId, repo.Status).ShouldBe((dead, world.InstanceId, RepositoryStatus.Error), "a different person is a different identity, and a connection made before the old one died was beside it, not after it");
    }

    [Fact]
    public async Task Credentials_whose_repositories_their_successors_connection_cannot_take_do_not_keep_a_repairable_one_waiting()
    {
        // A refusal that does not heal by being asked again — the successor's connection already holds the project — is a failure of that candidate: it is set aside
        // after a few passes in a row, instead of being "repaired" every tick and holding a place at the front of a pass that a repairable credential could have.
        var stuck = new List<(Guid Dead, Guid Repository)>();

        for (var i = 0; i < 5; i++)
        {
            var world = await _kit.SeedWorldAsync();
            var sibling = await _kit.SeedSiblingInstanceAsync(world);
            var revoked = DateTimeOffset.UtcNow.AddDays(-10);
            var dead = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-dead", CredentialStatus.Revoked, createdDate: revoked, id: FrontOfTheQueue());
            var successor = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-successor", instanceId: sibling, createdDate: revoked.AddDays(1));
            var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error, name: "api");
            await _kit.SeedRepositoryAsync(world, successor, instanceId: sibling, name: "api");

            stuck.Add((dead, repoId));
        }

        var elsewhere = await _kit.SeedWorldAsync();
        var movableSibling = await _kit.SeedSiblingInstanceAsync(elsewhere);
        var movableDead = await _kit.SeedCredentialAsync(elsewhere, elsewhere.OwnerId, "glpat-movable-dead", CredentialStatus.Revoked, createdDate: DateTimeOffset.UtcNow.AddDays(-10));
        var movableSuccessor = await _kit.SeedCredentialAsync(elsewhere, elsewhere.OwnerId, "glpat-movable-successor", instanceId: movableSibling, createdDate: DateTimeOffset.UtcNow.AddDays(-9));
        var movable = await _kit.SeedRepositoryAsync(elsewhere, movableDead, RepositoryStatus.Error);
        var passes = 0;

        while ((await _kit.LoadRepositoryAsync(movable)).CredentialId != movableSuccessor && passes < MaxSweeps)
        {
            passes++;

            using var scope = _kit.Fixture.BeginScope();

            await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand { BatchSize = 1 });
        }

        (await _kit.LoadRepositoryAsync(movable)).CredentialId.ShouldBe(movableSuccessor, "the repairable credential was reached however many refused ones sit ahead of it");
        passes.ShouldBeLessThan(MaxSweeps);

        foreach (var (dead, repoId) in stuck) (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead, "a refused repository stays exactly where it was");

        await DisposeStuckAsync(stuck.Select(s => s.Repository));
    }

    /// <summary>The sweep is deployment-wide: a binding that can never be repaired must not stay a candidate for every later test.</summary>
    private async Task DisposeStuckAsync(IEnumerable<Guid> repositoryIds)
    {
        var ids = repositoryIds.ToList();

        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Repository.Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, DateTimeOffset.UtcNow));
    }

    /// <summary>An id that sorts ahead of any random one: the sweep reads candidates in id order.</summary>
    private static Guid FrontOfTheQueue() => Guid.Parse($"00000000-0000-0000-0000-{Guid.NewGuid().ToString("N")[..12]}");

    [Fact]
    public async Task A_group_hook_stays_with_its_connection_and_a_credential_holding_only_one_is_not_repaired_across_connections()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var revoked = DateTimeOffset.UtcNow.AddDays(-10);
        var deadWithHookOnly = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-dead-hook", CredentialStatus.Revoked, createdDate: revoked);
        var deadWithRepository = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-dead-repo", CredentialStatus.Revoked, createdDate: revoked);
        var hookOnly = await _kit.SeedConnectionWebhookAsync(world, deadWithHookOnly, RepositoryWebhookRegistrationStatus.DeadLettered, ownerPath: "only-hook");
        var hookAndRepository = await _kit.SeedConnectionWebhookAsync(world, deadWithRepository, RepositoryWebhookRegistrationStatus.DeadLettered, ownerPath: "with-repo");
        var repoId = await _kit.SeedRepositoryAsync(world, deadWithRepository, RepositoryStatus.Error);
        var before = await DrainedStrandedAsync();

        await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owners-on-sibling", instanceId: sibling, createdDate: revoked.AddDays(1));

        (await StrandedAsync()).ShouldBe(before, "a credential with nothing but a group hook, whose owner connected only to a sibling, has no successor the hook could follow, so it is not a candidate");

        await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admins-on-sibling", instanceId: sibling, createdDate: revoked.AddDays(1));

        (await StrandedAsync()).ShouldBe(before + 1, "one that still holds a repository is");

        await SweepUntilAsync(async () => (await _kit.LoadRepositoryAsync(repoId)).ProviderInstanceId == sibling);

        (await _kit.LoadRepositoryAsync(repoId)).ProviderInstanceId.ShouldBe(sibling);
        (await _kit.LoadConnectionWebhookAsync(hookAndRepository)).CredentialId.ShouldBe(deadWithRepository, "the hook is keyed to its connection and does not move to a credential of another");
        (await _kit.LoadConnectionWebhookAsync(hookOnly)).CredentialId.ShouldBe(deadWithHookOnly);
        (await StrandedAsync()).ShouldBe(before, "once the repository has gone, nothing is left for the sweep to repair again and again");
    }

    [Fact]
    public async Task Dependents_follow_a_repository_to_the_credential_and_connection_it_moved_to()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);

        await RelinkAllAsync(world, world.OwnerId, dead, target);

        (await DependentsAsync(world, world.OwnerId, dead)).Repositories.ShouldBeEmpty();
        var now = await DependentsAsync(world, world.OwnerId, target);
        now.Repositories.Select(r => r.Id).ShouldBe(new[] { repoId });
        now.ProviderInstanceId.ShouldBe(sibling);
    }

    // ── Reads and seeds ───────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<RelinkSuggestion>> CandidatesAsync(BindingMoveKit.World world, Guid userId, Guid providerInstanceId, Guid? leaving = null)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new GetRelinkCandidatesQuery { ProviderInstanceId = providerInstanceId, FromCredentialId = leaving });
    }

    private async Task<string> NameOfAsync(Guid userId)
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().User.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Name).SingleAsync();
    }

    /// <summary>The act-as reader the workflow engine and the picker both go through: the identity a person may act with on a connection, or null.</summary>
    private async Task<UserProviderIdentity?> ResolveActAsAsync(Guid userId, Guid providerInstanceId)
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<IActorIdentityResolver>().ResolveAsync(userId, providerInstanceId, CancellationToken.None);
    }

    /// <summary>The users the author picker offers for the repository, by id.</summary>
    private async Task<Guid[]> ActAsAuthorsAsync(BindingMoveKit.World world, Guid repositoryId)
    {
        using var scope = _kit.Fixture.BeginScope();

        return (await scope.Resolve<IActorIdentityResolver>().ListCandidatesAsync(repositoryId, world.TeamId, CancellationToken.None)).Select(c => c.UserId).ToArray();
    }

    /// <summary>The credential list as the SPA reads it — the health it derives is what a person sees on each row.</summary>
    private async Task<IReadOnlyList<CredentialSummary>> ListCredentialsAsync(BindingMoveKit.World world, Guid userId)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new ListCredentialsQuery());
    }

    private async Task<CredentialDependents> DependentsAsync(BindingMoveKit.World world, Guid userId, Guid credentialId)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new GetCredentialDependentsQuery { CredentialId = credentialId });
    }

    /// <summary>The sweep's backlog over this database. Compared before and after, never against a number: other tests' rows are in it.</summary>
    private async Task<int> StrandedAsync()
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<IStrandedCredentialCensus>().CountAsync(CancellationToken.None);
    }

    /// <summary>
    /// The backlog once the sweep has done everything it can: what other tests left behind that it can repair is repaired first, so the sweeps this test
    /// then runs repair its own rows and nobody else's, and a count taken before them can be compared with one taken after.
    /// </summary>
    private async Task<int> DrainedStrandedAsync()
    {
        var count = await StrandedAsync();

        for (var pass = 1; pass <= MaxSweeps; pass++)
        {
            using (var scope = _kit.Fixture.BeginScope()) await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand { BatchSize = 200 });

            var next = await StrandedAsync();

            if (next == count) return count;

            count = next;
        }

        return count;
    }

    private async Task SoftDeleteCredentialAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Credential.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedDate, DateTimeOffset.UtcNow));
    }
}
