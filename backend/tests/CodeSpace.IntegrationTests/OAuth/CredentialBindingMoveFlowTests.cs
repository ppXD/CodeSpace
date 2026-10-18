using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// What happens to the repositories and group hooks bound through a credential when it is Disconnected from the
/// Providers modal, in either order of the two things a person does to swap a connection: connect the new one and
/// disconnect the old one.
///
/// <para>Disconnect-then-connect always carried the bindings forward (the connect path moves everything the owner's
/// revoked credentials still hold). Connect-then-disconnect did not: the owner's other connection was already live, so
/// the connect had nothing to carry, and the recurring repair only follows a connection made after the revocation. The
/// repository stayed on a revoked row — Error, every run refused — for good, and the only way back was somebody else
/// finding the Re-link button. The end state of the two orders must be the same, so a Disconnect that names no target
/// sends the bindings where the owner's one other connection makes obvious, and one that names a target sends them there,
/// in the same unit of work as the revoke.</para>
///
/// <para>Real Postgres, the real mediator pipeline for connect, token link and disconnect (authorization behaviors
/// included), and the run's own read (<c>IAgentWorkspaceResolver</c>) through the real GitLab auth strategies. Only the
/// provider's edges are the fixture's test doubles.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialBindingMoveFlowTests
{
    private readonly BindingMoveKit _kit;

    public CredentialBindingMoveFlowTests(PostgresFixture fixture) { _kit = new BindingMoveKit(fixture); }

    [Fact]
    public async Task Disconnecting_the_token_after_connecting_oauth_beside_it_carries_its_repository_and_hook_to_the_oauth_connection()
    {
        var world = await _kit.SeedWorldAsync();
        var token = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-old");
        var repoId = await _kit.SeedRepositoryAsync(world, token);
        var hookId = await _kit.SeedConnectionWebhookAsync(world, token, RepositoryWebhookRegistrationStatus.Registered);

        var oauth = await _kit.ConnectOAuthAsync(world, world.OwnerId, "at-new");

        var result = await _kit.DisconnectAsync(world, world.OwnerId, token);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(oauth, "the owner's other connection takes over, as it would had they connected it after the disconnect");
        repo.Status.ShouldBe(RepositoryStatus.Active);
        repo.LastError.ShouldBeNull();
        (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("at-new", "the next run clones through the connection that took over");
        (await _kit.LoadConnectionWebhookAsync(hookId)).CredentialId.ShouldBe(oauth, "a group hook records the credential it registers and deletes through");

        result.MovedRepositoryCount.ShouldBe(1);
        result.MovedConnectionWebhookCount.ShouldBe(1);
        result.MovedToCredentialId.ShouldBe(oauth);
        result.AffectedRepositoryCount.ShouldBe(0, "nothing was left to break, so the Disconnect dialog has nothing to warn about");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_orders_of_swapping_a_connection_end_in_the_same_place(bool connectFirst)
    {
        var world = await _kit.SeedWorldAsync();
        var token = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-old");
        var repoId = await _kit.SeedRepositoryAsync(world, token);
        Guid oauth;

        if (connectFirst)
        {
            oauth = await _kit.ConnectOAuthAsync(world, world.OwnerId, "at-swapped");
            await _kit.DisconnectAsync(world, world.OwnerId, token);
        }
        else
        {
            await _kit.DisconnectAsync(world, world.OwnerId, token);
            oauth = await _kit.ConnectOAuthAsync(world, world.OwnerId, "at-swapped");
        }

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(oauth);
        repo.Status.ShouldBe(RepositoryStatus.Active);
        repo.LastError.ShouldBeNull();
        (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("at-swapped");
    }

    public enum Ambiguity { TwoOtherLiveConnections, OnlyAnotherPersonsConnection, OnlyAConnectionOnAnotherInstance, OnlyARevokedConnection, NoOtherConnection, OnlyATeamServiceToken }

    [Theory]
    [InlineData(Ambiguity.TwoOtherLiveConnections)]
    [InlineData(Ambiguity.OnlyAnotherPersonsConnection)]
    [InlineData(Ambiguity.OnlyAConnectionOnAnotherInstance)]
    [InlineData(Ambiguity.OnlyARevokedConnection)]
    [InlineData(Ambiguity.NoOtherConnection)]
    [InlineData(Ambiguity.OnlyATeamServiceToken)]
    public async Task A_disconnect_that_names_no_target_moves_nothing_unless_the_owner_has_exactly_one_other_live_connection(Ambiguity shape)
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-dying");
        var repoId = await _kit.SeedRepositoryAsync(world, dead);

        await SeedAroundAsync(world, shape);

        var result = await _kit.DisconnectAsync(world, world.OwnerId, dead);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(dead, $"{shape}: no single successor is obvious, so the repository stays where a person put it");
        repo.Status.ShouldBe(RepositoryStatus.Error);
        repo.LastError.ShouldBe(CredentialRevocationService.DisconnectedRepositoryError);
        result.MovedRepositoryCount.ShouldBe(0);
        result.MovedToCredentialId.ShouldBeNull();
        result.AffectedRepositoryCount.ShouldBe(1, "the Disconnect dialog still warns about what it broke");

        var failure = await Should.ThrowAsync<CredentialDisconnectedException>(() => _kit.ResolveCloneTokenAsync(world, repoId));
        failure.CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task A_team_service_token_never_moves_on_its_own_even_beside_another_team_token()
    {
        // Two team tokens are rarely interchangeable: a group token reaches only its group's projects. Choosing between them is
        // somebody's decision, not a default.
        var world = await _kit.SeedWorldAsync();
        var groupToken = await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-group-a", ownership: CredentialOwnership.TeamService);
        await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-group-b", ownership: CredentialOwnership.TeamService);
        var repoId = await _kit.SeedRepositoryAsync(world, groupToken);

        var result = await _kit.DisconnectAsync(world, world.AdminId, groupToken);

        (await _kit.LoadRepositoryAsync(repoId)).Status.ShouldBe(RepositoryStatus.Error);
        result.MovedRepositoryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Naming_the_target_moves_the_bindings_even_when_the_owner_holds_two_other_connections()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-dying");
        var wrongOne = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-not-this");
        var chosen = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-this-one");
        var repoId = await _kit.SeedRepositoryAsync(world, dead);

        var result = await _kit.DisconnectAsync(world, world.OwnerId, dead, moveBindingsTo: chosen);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(chosen);
        repo.Status.ShouldBe(RepositoryStatus.Active);
        (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-this-one");
        (await _kit.LoadCredentialAsync(dead)).Status.ShouldBe(CredentialStatus.Revoked, "the disconnect itself still happened");
        (await _kit.LoadCredentialAsync(wrongOne)).Status.ShouldBe(CredentialStatus.Active);
        result.MovedToCredentialId.ShouldBe(chosen);
    }

    public enum Target { AnotherPersonsConnection, OnAnotherInstance, Revoked, TheCredentialItself, ATeamServiceToken, ForeignToTheTeam, NoSuchCredential }

    [Theory]
    [InlineData(Target.AnotherPersonsConnection)]
    [InlineData(Target.OnAnotherInstance)]
    [InlineData(Target.Revoked)]
    [InlineData(Target.TheCredentialItself)]
    [InlineData(Target.ATeamServiceToken)]
    [InlineData(Target.ForeignToTheTeam)]
    [InlineData(Target.NoSuchCredential)]
    public async Task A_target_the_move_cannot_accept_is_refused_before_anything_is_revoked(Target shape)
    {
        var world = await _kit.SeedWorldAsync();
        var oauth = await _kit.ConnectOAuthAsync(world, world.OwnerId, "at-dying");
        var repoId = await _kit.SeedRepositoryAsync(world, oauth);
        var target = await SeedTargetAsync(world, shape, oauth);

        var refusal = await Record.ExceptionAsync(() => _kit.DisconnectAsync(world, world.OwnerId, oauth, moveBindingsTo: target));

        refusal.ShouldNotBeNull($"{shape}: the move is refused");
        refusal.ShouldBeOfType(shape is Target.NoSuchCredential or Target.ForeignToTheTeam ? typeof(KeyNotFoundException) : typeof(InvalidOperationException), $"{shape}: {refusal.Message}");

        var credential = await _kit.LoadCredentialAsync(oauth);

        credential.Status.ShouldBe(CredentialStatus.Active, $"{shape}: a refused move leaves the credential connected");
        credential.EncryptedPayload.ShouldNotBeNullOrEmpty($"{shape}: its token material is intact");
        world.OAuth.LastRevoke.ShouldBeNull($"{shape}: the provider was never asked to revoke the grant");
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(oauth);
        (await _kit.LoadRepositoryAsync(repoId)).Status.ShouldBe(RepositoryStatus.Active);
    }

    [Theory]
    [InlineData(RepositoryWebhookRegistrationStatus.DeadLettered, RepositoryWebhookRegistrationStatus.Enqueued, true)]
    [InlineData(RepositoryWebhookRegistrationStatus.Failed, RepositoryWebhookRegistrationStatus.Enqueued, true)]
    [InlineData(RepositoryWebhookRegistrationStatus.Registered, RepositoryWebhookRegistrationStatus.Registered, true)]
    [InlineData(RepositoryWebhookRegistrationStatus.Cancelled, RepositoryWebhookRegistrationStatus.Cancelled, false)]
    public async Task A_group_hook_follows_the_disconnect_and_a_buried_one_is_revived_and_dispatched(RepositoryWebhookRegistrationStatus before, RepositoryWebhookRegistrationStatus after, bool follows)
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-dying");
        var successor = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-successor");
        var hookId = await _kit.SeedConnectionWebhookAsync(world, dead, before);

        using var dispatches = _kit.Jobs().ManualExecution();
        var result = await _kit.DisconnectAsync(world, world.OwnerId, dead);

        var hook = await _kit.LoadConnectionWebhookAsync(hookId);

        hook.CredentialId.ShouldBe(follows ? successor : dead, $"a {before} hook {(follows ? "is in service, so it follows" : "is retired, so it stays")}");
        hook.RegistrationStatus.ShouldBe(after);
        hook.Attempts.ShouldBe(after == RepositoryWebhookRegistrationStatus.Enqueued ? 0 : BindingMoveKit.SeededHookAttempts, "a revival resets the backoff ladder, exactly as a bind's does");
        _kit.WasDispatched(hookId).ShouldBe(after == RepositoryWebhookRegistrationStatus.Enqueued, "only a revived hook is handed to the registrar, and only once the disconnect committed");
        result.MovedConnectionWebhookCount.ShouldBe(follows ? 1 : 0);
        result.MovedToCredentialId.ShouldBe(follows ? successor : null);
    }

    [Theory]
    [InlineData(RepositoryStatus.Paused, RepositoryStatus.Paused)]
    [InlineData(RepositoryStatus.Unreachable, RepositoryStatus.Unreachable)]
    [InlineData(RepositoryStatus.Error, RepositoryStatus.Active)]
    public async Task A_repository_keeps_the_status_that_is_its_own_when_it_moves(RepositoryStatus before, RepositoryStatus after)
    {
        // Paused and Unreachable say something about the repository, not about its credential: a person paused it, the provider
        // could not reach it. Only the Error a disconnect sets is lifted by finding a live credential.
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-dying");
        var successor = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-successor");
        var repoId = await _kit.SeedRepositoryAsync(world, dead, before);

        await _kit.DisconnectAsync(world, world.OwnerId, dead);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(successor);
        repo.Status.ShouldBe(after);
    }

    [Fact]
    public async Task A_disconnect_with_nothing_bound_reports_nothing_moved_and_still_revokes()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-unused");
        await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-spare");

        var result = await _kit.DisconnectAsync(world, world.OwnerId, dead);

        result.MovedRepositoryCount.ShouldBe(0);
        result.MovedConnectionWebhookCount.ShouldBe(0);
        result.MovedToCredentialId.ShouldBeNull("nothing moved, so there is no destination to report");
        (await _kit.LoadCredentialAsync(dead)).Status.ShouldBe(CredentialStatus.Revoked);
    }

    [Fact]
    public async Task Naming_a_target_for_an_already_disconnected_credential_moves_what_is_still_bound_to_it()
    {
        // The Disconnect that stranded these ran before this change, or without a target. Saying where they go now is the
        // same request, and it is idempotent in the way the cascade it replaces always was.
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-old");
        var repoId = await _kit.SeedRepositoryAsync(world, dead);
        await _kit.DisconnectAsync(world, world.OwnerId, dead);
        (await _kit.LoadRepositoryAsync(repoId)).Status.ShouldBe(RepositoryStatus.Error, "fixture check: the first disconnect stranded it");

        var spare = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-spare");
        var again = await _kit.DisconnectAsync(world, world.OwnerId, dead, moveBindingsTo: spare);

        again.MovedRepositoryCount.ShouldBe(1);
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(spare);
        (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-spare");
    }

    [Fact]
    public async Task A_member_disconnects_their_own_credential_and_moves_its_bindings_to_their_other_one()
    {
        // The Personal tab's Disconnect is every member's own, and it stays so with a target: moving what is bound through your
        // own credential onto your own other credential gives the team no power over repositories it did not already have —
        // connecting the second one after the disconnect would have carried them the same way.
        var world = await _kit.SeedWorldAsync();
        var first = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-first");
        var second = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-second");
        var repoId = await _kit.SeedRepositoryAsync(world, first);

        var result = await _kit.DisconnectAsync(world, world.MemberId, first, moveBindingsTo: second);

        result.MovedRepositoryCount.ShouldBe(1);
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(second);
    }

    [Theory]
    [InlineData(TeamRole.Viewer)]
    [InlineData(TeamRole.Member)]
    public async Task Below_admin_a_move_does_not_make_a_teammates_credential_disconnectable(TeamRole role)
    {
        var world = await _kit.SeedWorldAsync();
        var caller = await _kit.SeedMemberAsync(world, role);
        var owners = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner-a");
        var ownersOther = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner-b");
        await _kit.SeedRepositoryAsync(world, owners);

        await Should.ThrowAsync<TenantAccessDeniedException>(() => _kit.DisconnectAsync(world, caller, owners, moveBindingsTo: ownersOther));

        (await _kit.LoadCredentialAsync(owners)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task An_admin_disconnecting_a_teammates_credential_may_move_it_only_to_that_teammates_other_connection()
    {
        var world = await _kit.SeedWorldAsync();
        var owners = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner-a");
        var ownersOther = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-owner-b");
        var admins = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin");
        var repoId = await _kit.SeedRepositoryAsync(world, owners);

        var refusal = await Record.ExceptionAsync(() => _kit.DisconnectAsync(world, world.AdminId, owners, moveBindingsTo: admins));

        refusal.ShouldBeOfType<InvalidOperationException>("the repositories would silently start acting as the Admin");
        (await _kit.LoadCredentialAsync(owners)).Status.ShouldBe(CredentialStatus.Active, "refused before anything was revoked");

        var result = await _kit.DisconnectAsync(world, world.AdminId, owners, moveBindingsTo: ownersOther);

        result.MovedRepositoryCount.ShouldBe(1);
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(ownersOther);
    }

    [Fact]
    public async Task A_target_held_by_someone_without_standing_in_the_team_is_refused()
    {
        // The departed-token sweep retires a personal credential whose person has no standing in the team. Moving
        // repositories onto one would hand them to the next tick's retirement, stranded again.
        var world = await _kit.SeedWorldAsync();
        var stranger = await SeedStrangerAsync();
        var disconnecting = await _kit.SeedCredentialAsync(world, stranger, "glpat-stranger-a");
        var target = await _kit.SeedCredentialAsync(world, stranger, "glpat-stranger-b");
        await _kit.SeedRepositoryAsync(world, disconnecting);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => _kit.DisconnectAsync(world, world.AdminId, disconnecting, moveBindingsTo: target));

        refusal.Message.ShouldContain("no longer has standing");
        (await _kit.LoadCredentialAsync(disconnecting)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task An_admin_disconnecting_a_teammates_credential_with_no_target_moves_nothing_even_beside_their_one_other_connection()
    {
        // The default is the answer reconnecting would give, and only the person whose connection it is reconnects. An Admin
        // cutting off a teammate's token must not have the repository carried onto the teammate's other grant instead: it would go
        // on acting as that person, with no way for the Admin to say otherwise. Naming the target is how an Admin moves it.
        var world = await _kit.SeedWorldAsync();
        var theirs = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-first");
        await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-member-second");
        var adminsOwn = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-admin-only-one");
        var repoId = await _kit.SeedRepositoryAsync(world, theirs);

        var result = await _kit.DisconnectAsync(world, world.AdminId, theirs);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(theirs);
        repo.Status.ShouldBe(RepositoryStatus.Error);
        result.MovedRepositoryCount.ShouldBe(0);
        result.MovedToCredentialId.ShouldBeNull();
        result.AffectedRepositoryCount.ShouldBe(1);
        (await _kit.LoadCredentialAsync(theirs)).Status.ShouldBe(CredentialStatus.Revoked, "the disconnect itself still happened");
        (await _kit.LoadCredentialAsync(adminsOwn)).Status.ShouldBe(CredentialStatus.Active, "and the Admin's own single connection was never a candidate: it would have made the repository act as the Admin");
    }

    [Fact]
    public async Task Disconnecting_a_credential_that_is_already_disconnected_with_no_target_moves_nothing_on_its_own()
    {
        // A, B and C are the owner's. A goes while two others are live: nothing moves, the repository turns Error. B goes.
        // Disconnecting A again — an idempotent retry, a double click — must not now find C the sole survivor and carry a
        // repository the owner left stranded onto a connection they never chose for it.
        var world = await _kit.SeedWorldAsync();
        var a = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-a");
        var b = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-b");
        var c = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-c");
        var repoId = await _kit.SeedRepositoryAsync(world, a);

        await _kit.DisconnectAsync(world, world.OwnerId, a);
        await _kit.DisconnectAsync(world, world.OwnerId, b);

        var again = await _kit.DisconnectAsync(world, world.OwnerId, a);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        again.MovedRepositoryCount.ShouldBe(0);
        again.MovedToCredentialId.ShouldBeNull();
        repo.CredentialId.ShouldBe(a);
        repo.Status.ShouldBe(RepositoryStatus.Error);
        (await _kit.LoadCredentialAsync(c)).Status.ShouldBe(CredentialStatus.Active);
    }

    [Fact]
    public async Task The_implicit_successor_is_never_a_credential_the_move_would_refuse()
    {
        // Disconnect must never fail because of a default it chose for itself. The person with no standing in the team has one
        // other live token — the sole candidate — and the move refuses it, so the default is no default at all.
        var world = await _kit.SeedWorldAsync();
        var stranger = await SeedStrangerAsync();
        var disconnecting = await _kit.SeedCredentialAsync(world, stranger, "glpat-stranger-a");
        await _kit.SeedCredentialAsync(world, stranger, "glpat-stranger-b");
        var from = await _kit.LoadCredentialAsync(disconnecting);

        using var scope = _kit.Fixture.BeginScope();

        (await scope.Resolve<ICredentialSuccessionService>().FindOnlyOtherConnectionAsync(from, stranger, CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Unlinking_the_identity_ends_the_connection_the_way_a_disconnect_does(int liveConnections, bool moves)
    {
        // The Connected Identities page's Unlink retires the credential behind the identity through the same cascade as a
        // Disconnect, so the same shape strands the same repository: the person connected OAuth beside their token and removed
        // the OAuth identity. Their one other live connection is where a Disconnect would have sent it.
        var world = await _kit.SeedWorldAsync();
        var token = await _kit.LinkTokenAsync(world, world.OwnerId, "glpat-first");
        var oauth = await _kit.ConnectOAuthAsync(world, world.OwnerId, "at-linked");

        for (var i = 1; i < liveConnections; i++) await _kit.SeedCredentialAsync(world, world.OwnerId, $"glpat-extra-{i}");

        var repoId = await _kit.SeedRepositoryAsync(world, oauth);
        var hookId = await _kit.SeedConnectionWebhookAsync(world, oauth, RepositoryWebhookRegistrationStatus.Registered);

        await _kit.UnlinkIdentityAsync(world, world.OwnerId);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        (await _kit.LoadCredentialAsync(oauth)).DeletedDate.ShouldNotBeNull("the unlink still retires the credential behind the identity");
        repo.CredentialId.ShouldBe(moves ? token : oauth);
        repo.Status.ShouldBe(moves ? RepositoryStatus.Active : RepositoryStatus.Error);
        (await _kit.LoadConnectionWebhookAsync(hookId)).CredentialId.ShouldBe(moves ? token : oauth);

        if (moves) (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-first", "the next run clones through the connection that took over");
    }

    public enum UsageShape { OwnerWithOneOtherConnection, OwnerWithTwoOtherConnections, AdminForATeammateWithOneOtherConnection, TeamServiceTokenBesideAnother }

    [Theory]
    [InlineData(UsageShape.OwnerWithOneOtherConnection)]
    [InlineData(UsageShape.OwnerWithTwoOtherConnections)]
    [InlineData(UsageShape.AdminForATeammateWithOneOtherConnection)]
    [InlineData(UsageShape.TeamServiceTokenBesideAnother)]
    public async Task The_disconnect_preview_says_what_the_disconnect_then_does(UsageShape shape)
    {
        // The confirm dialog quotes the preview. A preview that still said "this will break N repositories" for a disconnect
        // that moves them all is a warning about something that will not happen.
        var world = await _kit.SeedWorldAsync();
        var (caller, owner, ownership) = shape switch
        {
            UsageShape.AdminForATeammateWithOneOtherConnection => (world.AdminId, (Guid?)world.MemberId, CredentialOwnership.Personal),
            UsageShape.TeamServiceTokenBesideAnother => (world.AdminId, (Guid?)null, CredentialOwnership.TeamService),
            _ => (world.OwnerId, (Guid?)world.OwnerId, CredentialOwnership.Personal)
        };

        var dying = await _kit.SeedCredentialAsync(world, owner, "glpat-dying", ownership: ownership);

        for (var i = 0; i < (shape == UsageShape.OwnerWithTwoOtherConnections ? 2 : 1); i++) await _kit.SeedCredentialAsync(world, owner, $"glpat-other-{i}", ownership: ownership);

        await _kit.SeedRepositoryAsync(world, dying);
        await _kit.SeedRepositoryAsync(world, dying);

        var usage = await _kit.UsageAsync(world, caller, dying);
        var result = await _kit.DisconnectAsync(world, caller, dying);

        usage.ActiveRepositoryCount.ShouldBe(result.AffectedRepositoryCount, $"{shape}: the repositories the preview warns about are the ones the disconnect then breaks");
        usage.WillMoveRepositoryCount.ShouldBe(result.MovedRepositoryCount, $"{shape}: and the ones it says will move are the ones that do");
        usage.WillMoveToDisplayName.ShouldBe(result.MovedToCredentialId is Guid movedTo ? (await _kit.LoadCredentialAsync(movedTo)).DisplayName : null, $"{shape}: naming where");
    }

    // ── Shapes ────────────────────────────────────────────────────────────────────

    private async Task SeedAroundAsync(BindingMoveKit.World world, Ambiguity shape)
    {
        switch (shape)
        {
            case Ambiguity.TwoOtherLiveConnections:
                await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-other-1");
                await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-other-2");
                break;
            case Ambiguity.OnlyAnotherPersonsConnection:
                await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-teammates");
                break;
            case Ambiguity.OnlyAConnectionOnAnotherInstance:
                await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-elsewhere", instanceId: await _kit.SeedOtherInstanceAsync(world));
                break;
            case Ambiguity.OnlyARevokedConnection:
                await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-gone", CredentialStatus.Revoked);
                break;
            case Ambiguity.OnlyATeamServiceToken:
                await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team", ownership: CredentialOwnership.TeamService);
                break;
            case Ambiguity.NoOtherConnection:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "a shape the test does not seed");
        }
    }

    /// <summary>The Guid a disconnect is told to send the bindings to, for each way of naming something it must not accept.</summary>
    private async Task<Guid> SeedTargetAsync(BindingMoveKit.World world, Target shape, Guid disconnecting)
    {
        switch (shape)
        {
            case Target.AnotherPersonsConnection: return await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-teammates");
            case Target.OnAnotherInstance: return await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-elsewhere", instanceId: await _kit.SeedOtherInstanceAsync(world));
            case Target.Revoked: return await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-gone", CredentialStatus.Revoked);
            case Target.TheCredentialItself: return disconnecting;
            case Target.ATeamServiceToken: return await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team", ownership: CredentialOwnership.TeamService);
            case Target.ForeignToTheTeam: return await SeedCredentialInAnotherTeamAsync();
            case Target.NoSuchCredential: return Guid.NewGuid();
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, "a shape the test does not seed");
        }
    }

    private async Task<Guid> SeedCredentialInAnotherTeamAsync()
    {
        var other = await _kit.SeedWorldAsync();

        return await _kit.SeedCredentialAsync(other, other.OwnerId, "glpat-other-team");
    }

    /// <summary>A person with an account and no membership in the team — what a departed member is, before the sweep retires what they lent.</summary>
    private async Task<Guid> SeedStrangerAsync()
    {
        var stranger = new CodeSpace.Core.Persistence.Entities.User { Id = Guid.NewGuid(), Email = $"stranger-{Guid.NewGuid():N}@x", Name = "stranger" };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.User.Add(stranger);
        await db.SaveChangesAsync();

        return stranger.Id;
    }
}
