using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Invitations;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Invitations;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Queries.Invitations;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Invitations;

/// <summary>
/// The whole invitation lifecycle against a real database, driven through the mediator so every
/// authorization behaviour runs exactly as it does in production.
///
/// <para>This is the path by which a second person can use the product at all, so the tests are
/// written as the things that must be true of it rather than as coverage of its methods: a link
/// works once, a stranger learns nothing from a wrong guess, nobody grants themselves a promotion,
/// and whoever joins gets a workspace of their own.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class TeamInvitationFlowTests
{
    private readonly PostgresFixture _fixture;

    public TeamInvitationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task An_invited_stranger_becomes_a_member_with_a_workspace_of_their_own()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var email = $"newcomer-{Guid.NewGuid():N}@x";

        var invite = await InviteAsync(team, email, TeamRole.Member).ConfigureAwait(false);
        var session = await AcceptAsync(TokenOf(invite), "Maya Chen", "correct-horse-battery").ConfigureAwait(false);

        session.Token.ShouldNotBeNullOrWhiteSpace();
        session.User.Email.ShouldBe(email);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = await db.User.SingleAsync(u => u.Email == email).ConfigureAwait(false);

        var membership = await db.TeamMembership.SingleAsync(m => m.TeamId == team.TeamId && m.UserId == user.Id).ConfigureAwait(false);
        membership.Role.ShouldBe(TeamRole.Member);

        // Migration 0008 holds "one personal team per user" for accounts that already existed. Nothing
        // created one for a NEW account, because until this path there were no new accounts.
        var personal = await db.Team.SingleAsync(t => t.PersonalForUserId == user.Id && t.Kind == TeamKind.Personal).ConfigureAwait(false);
        personal.Name.ShouldBe("Personal");
        (await db.TeamMembership.AnyAsync(m => m.TeamId == personal.Id && m.UserId == user.Id && m.Role == TeamRole.Owner).ConfigureAwait(false)).ShouldBeTrue();

        // And its default project. The sidebar gives a personal team a Projects row like any other, and
        // that page tells whoever opens it one was auto-created for this team. It is the workspace a new
        // account lands in first, so it is the worst place for that to be untrue — and the least likely
        // to ever have a repository bound into it, which was the only thing that used to make it true.
        //
        // This also pins the wiring: the personal team is built by the provisioning service now, and a
        // path that went back to constructing one by hand would produce no project here.
        var projects = await db.Project.AsNoTracking().Where(p => p.TeamId == personal.Id && p.DeletedDate == null).Select(p => p.Slug).ToListAsync().ConfigureAwait(false);

        projects.ShouldBe(["default"], customMessage: "a personal workspace opens with exactly one project — the default its Projects page says is there");
    }

    [Fact]
    public async Task A_link_works_once()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"once-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);
        var token = TokenOf(invite);

        await AcceptAsync(token, "First Taker", "correct-horse-battery").ConfigureAwait(false);

        var second = async () => await AcceptAsync(token, "Second Taker", "correct-horse-battery").ConfigureAwait(false);

        await second.ShouldThrowAsync<InvitationNotUsableException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task An_expired_link_is_dead_without_anything_having_to_sweep_it()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"stale-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);

        await ExpireAsync(invite.InvitationId).ConfigureAwait(false);

        var preview = async () => await PreviewAsync(TokenOf(invite)).ConfigureAwait(false);

        await preview.ShouldThrowAsync<InvitationNotUsableException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_revoked_link_stops_working_immediately()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"revoked-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);

        using (var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId))
        {
            await scope.Resolve<IMediator>().Send(new RevokeTeamInvitationCommand { InvitationId = invite.InvitationId }).ConfigureAwait(false);
        }

        var preview = async () => await PreviewAsync(TokenOf(invite)).ConfigureAwait(false);

        await preview.ShouldThrowAsync<InvitationNotUsableException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Regenerating_kills_the_previous_link()
    {
        // The reason to regenerate is that the first link may be somewhere it should not be. If the old
        // one kept working, the operation would be theatre.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"rotate-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);
        var original = TokenOf(invite);

        CreateInvitationResult replacement;
        using (var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId))
        {
            replacement = await scope.Resolve<IMediator>().Send(new RegenerateTeamInvitationCommand { InvitationId = invite.InvitationId }).ConfigureAwait(false);
        }

        var stale = async () => await PreviewAsync(original).ConfigureAwait(false);
        await stale.ShouldThrowAsync<InvitationNotUsableException>().ConfigureAwait(false);

        (await PreviewAsync(TokenOf(replacement)).ConfigureAwait(false)).Email.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_wrong_guess_learns_nothing_about_the_team()
    {
        // The link is the credential, so a token that does not resolve must not distinguish itself from
        // one that expired — otherwise a guesser can map which tokens were once real.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"real-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);
        await ExpireAsync(invite.InvitationId).ConfigureAwait(false);

        var invented = async () => await PreviewAsync("not-a-real-token").ConfigureAwait(false);
        var expired = async () => await PreviewAsync(TokenOf(invite)).ConfigureAwait(false);

        var fromInvented = await invented.ShouldThrowAsync<InvitationNotUsableException>().ConfigureAwait(false);
        var fromExpired = await expired.ShouldThrowAsync<InvitationNotUsableException>().ConfigureAwait(false);

        fromInvented.Message.ShouldBe(fromExpired.Message);
        fromInvented.Code.ShouldBe(fromExpired.Code);
    }

    [Theory]
    [InlineData(TeamRole.Viewer)]
    [InlineData(TeamRole.Member)]
    public async Task Only_a_member_who_can_manage_members_may_invite(TeamRole role)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.UserFor(role), team.TeamId);
        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = "x@x", Role = TeamRole.Member }).ConfigureAwait(false);

        var denied = await act.ShouldThrowAsync<TenantAccessDeniedException>().ConfigureAwait(false);
        denied.Reason.ShouldContain("members.manage");
    }

    [Fact]
    public async Task Nobody_invites_above_their_own_standing()
    {
        // Otherwise an Admin is one acceptance away from being overruled in their own team.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Admin, team.TeamId);
        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = $"climb-{Guid.NewGuid():N}@x", Role = TeamRole.Owner }).ConfigureAwait(false);

        var thrown = await act.ShouldThrowAsync<InvitationRoleExceedsGranterException>().ConfigureAwait(false);
        thrown.Requested.ShouldBe(TeamRole.Owner);
        thrown.Granter.ShouldBe(TeamRole.Admin);
    }

    [Fact]
    public async Task An_admin_may_invite_at_their_own_rank()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Admin, team.TeamId);
        var result = await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = $"peer-{Guid.NewGuid():N}@x", Role = TeamRole.Admin }).ConfigureAwait(false);

        result.InviteUrl.ShouldContain("/invite/");
    }

    [Theory]
    [InlineData(TeamRole.Admin, TeamRole.Admin)]   // at your own rank, your own invitation is yours
    [InlineData(TeamRole.Admin, TeamRole.Member)]
    [InlineData(TeamRole.Owner, TeamRole.Owner)]
    public async Task An_invitation_you_sent_at_or_below_your_rank_can_be_regenerated_and_revoked(TeamRole actorRole, TeamRole invitationRole)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var actor = team.UserFor(actorRole);
        var invitationId = await SeedPendingInvitationAsync(team.TeamId, invitationRole, sentBy: actor).ConfigureAwait(false);

        await ManageAsync(actor, team.TeamId, new RegenerateTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);
        await ManageAsync(actor, team.TeamId, new RevokeTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);

        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).Status.ShouldBe(InvitationStatus.Revoked);
    }

    [Theory]
    [InlineData(TeamRole.Owner, TeamRole.Admin)]
    [InlineData(TeamRole.Owner, TeamRole.Member)]
    [InlineData(TeamRole.Admin, TeamRole.Member)]
    public async Task An_invitation_below_your_rank_can_be_managed_whoever_sent_it(TeamRole actorRole, TeamRole invitationRole)
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var sender = await SeedMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false);
        var invitationId = await SeedPendingInvitationAsync(team.TeamId, invitationRole, sentBy: sender).ConfigureAwait(false);

        await ManageAsync(team.UserFor(actorRole), team.TeamId, new RegenerateTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);
        await ManageAsync(team.UserFor(actorRole), team.TeamId, new RevokeTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);

        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).Status.ShouldBe(InvitationStatus.Revoked);
    }

    [Theory]
    [InlineData(false)]   // the escalation: regenerate someone else's Owner invitation, accept it with a puppet, be promoted
    [InlineData(true)]    // their own, sent while they were Owner — demoted since, it is above them now
    public async Task An_invitation_above_your_rank_cannot_be_regenerated_and_is_revocable_only_when_you_sent_it(bool sentByActor)
    {
        // Re-issuing an invitation above you would be a way up — for a demoted sender, a way back. Withdrawing one raises
        // nobody, so the sender keeps that, as they always had; someone else's stays with the ranks at or above it.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var sender = sentByActor ? team.Admin : await SeedMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false);
        var invitationId = await SeedPendingInvitationAsync(team.TeamId, TeamRole.Owner, sentBy: sender).ConfigureAwait(false);
        var before = await LoadInvitationAsync(invitationId).ConfigureAwait(false);

        var regenerate = async () => await ManageAsync(team.Admin, team.TeamId, new RegenerateTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);
        var revoke = async () => await ManageAsync(team.Admin, team.TeamId, new RevokeTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);

        var refusedRegenerate = await regenerate.ShouldThrowAsync<InvitationOutranksActorException>().ConfigureAwait(false);

        refusedRegenerate.Actor.ShouldBe(TeamRole.Admin);
        refusedRegenerate.InvitationRole.ShouldBe(TeamRole.Owner);
        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).TokenHash.ShouldBe(before.TokenHash, "a refused regenerate rotates nothing — the sender's own link still works");

        if (sentByActor)
        {
            await revoke().ConfigureAwait(false);
            (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).Status.ShouldBe(InvitationStatus.Revoked, "the sender may always withdraw what they sent");
            return;
        }

        (await revoke.ShouldThrowAsync<InvitationOutranksActorException>().ConfigureAwait(false)).Code.ShouldBe("invitation_outranks_actor");
        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).Status.ShouldBe(InvitationStatus.Pending, "a refused revoke withdraws nothing");
    }

    [Fact]
    public async Task A_peer_admins_admin_invitation_can_be_revoked_but_not_regenerated()
    {
        // Regenerating hands the caller a working link to a peer's grant — acting ACROSS their rank. Revoking raises
        // nobody: the worst it does is make the peer send the invitation again.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var peer = await SeedMemberAsync(team.TeamId, TeamRole.Admin).ConfigureAwait(false);
        var invitationId = await SeedPendingInvitationAsync(team.TeamId, TeamRole.Admin, sentBy: peer).ConfigureAwait(false);
        var before = await LoadInvitationAsync(invitationId).ConfigureAwait(false);

        var regenerate = async () => await ManageAsync(team.Admin, team.TeamId, new RegenerateTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);

        (await regenerate.ShouldThrowAsync<InvitationOutranksActorException>().ConfigureAwait(false)).Code.ShouldBe("invitation_outranks_actor");
        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).TokenHash.ShouldBe(before.TokenHash, "a refused regenerate rotates nothing");

        await ManageAsync(team.Admin, team.TeamId, new RevokeTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);

        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).Status.ShouldBe(InvitationStatus.Revoked);
    }

    [Theory]
    [InlineData(true)]    // a co-Owner's, the co-Owner still in the team
    [InlineData(false)]   // a departed Owner's — before this, nobody at all could withdraw it
    public async Task An_owner_regenerates_and_revokes_another_owners_owner_invitation(bool senderStillMember)
    {
        // Nobody ranks above an Owner to arbitrate, and an Owner may invite at Owner outright, so there is no climb to
        // stop — and a leaked Owner link must always have someone able to kill it.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var sender = senderStillMember ? await SeedMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false) : (await SeedUserAsync().ConfigureAwait(false)).Id;
        var invitationId = await SeedPendingInvitationAsync(team.TeamId, TeamRole.Owner, sentBy: sender).ConfigureAwait(false);

        await ManageAsync(team.Owner, team.TeamId, new RegenerateTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);
        await ManageAsync(team.Owner, team.TeamId, new RevokeTeamInvitationCommand { InvitationId = invitationId }).ConfigureAwait(false);

        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).Status.ShouldBe(InvitationStatus.Revoked);
    }

    [Fact]
    public async Task The_instance_admin_regenerates_and_revokes_an_owner_invitation_in_any_team()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var sender = await SeedMemberAsync(team.TeamId, TeamRole.Owner).ConfigureAwait(false);
        var invitationId = await SeedPendingInvitationAsync(team.TeamId, TeamRole.Owner, sentBy: sender).ConfigureAwait(false);
        var instanceAdmin = (await SeedUserAsync().ConfigureAwait(false)).Id;

        await ManageAsync(instanceAdmin, team.TeamId, new RegenerateTeamInvitationCommand { InvitationId = invitationId }, Roles.Admin).ConfigureAwait(false);
        await ManageAsync(instanceAdmin, team.TeamId, new RevokeTeamInvitationCommand { InvitationId = invitationId }, Roles.Admin).ConfigureAwait(false);

        (await LoadInvitationAsync(invitationId).ConfigureAwait(false)).Status.ShouldBe(InvitationStatus.Revoked);
    }

    [Fact]
    public async Task An_account_cannot_be_created_under_a_name_shaped_like_an_address()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"newcomer-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);

        var act = async () => await AcceptAsync(TokenOf(invite), $"victim-{Guid.NewGuid():N}@corp.test", "correct-horse-battery").ConfigureAwait(false);

        await act.ShouldThrowAsync<DisplayNameLooksLikeEmailException>().ConfigureAwait(false);
        (await PreviewAsync(TokenOf(invite)).ConfigureAwait(false)).Email.ShouldNotBeNullOrWhiteSpace("a refused acceptance spends nothing — the link still works");
    }

    [Theory]
    [InlineData(false)]   // another account's display name
    [InlineData(true)]    // another account's address (one with no '@', so only the uniqueness rule can catch it)
    public async Task An_account_cannot_be_created_under_an_identifier_another_live_account_signs_in_with(bool claimedAsAddress)
    {
        var claimed = $"Claimed {Guid.NewGuid():N}";
        await SeedUserAsync(email: claimedAsAddress ? claimed : $"holder-{Guid.NewGuid():N}@x", name: claimedAsAddress ? $"holder-{Guid.NewGuid():N}" : claimed).ConfigureAwait(false);
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"newcomer-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);

        var act = async () => await AcceptAsync(TokenOf(invite), $"  {claimed.ToUpperInvariant()} ", "correct-horse-battery").ConfigureAwait(false);

        await act.ShouldThrowAsync<DisplayNameTakenException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_name_only_a_departed_account_held_is_free_again()
    {
        var name = $"Departed {Guid.NewGuid():N}";
        var departed = await SeedUserAsync(email: $"gone-{Guid.NewGuid():N}@x", name: name).ConfigureAwait(false);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.User.SingleAsync(u => u.Id == departed.Id).ConfigureAwait(false)).DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"newcomer-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);

        var session = await AcceptAsync(TokenOf(invite), name, "correct-horse-battery").ConfigureAwait(false);

        session.User.Name.ShouldBe(name);
    }

    [Fact]
    public async Task The_preview_tells_only_the_invited_account_that_the_invitation_is_its_own()
    {
        // It used to say to anyone holding a token whether the address had an account at all — and anyone who can
        // open a workspace can mint a token for any address.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invited = await SeedUserAsync().ConfigureAwait(false);
        var bystander = await SeedUserAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, invited.Email.ToUpperInvariant(), TeamRole.Member).ConfigureAwait(false);

        (await PreviewAsync(TokenOf(invite)).ConfigureAwait(false)).ForSignedInAccount.ShouldBeFalse("anonymous");
        (await PreviewAsAsync(TokenOf(invite), bystander.Id).ConfigureAwait(false)).ForSignedInAccount.ShouldBeFalse("another account");
        (await PreviewAsAsync(TokenOf(invite), invited.Id).ConfigureAwait(false)).ForSignedInAccount.ShouldBeTrue("the invited account itself");
    }

    [Fact]
    public async Task A_personal_workspace_cannot_have_anyone_invited_into_it()
    {
        var team = await SeedTeamAsync(TeamKind.Personal).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = "x@x", Role = TeamRole.Member }).ConfigureAwait(false);

        await act.ShouldThrowAsync<PersonalTeamNotInvitableException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_must_sign_in_rather_than_set_a_password()
    {
        // Without this, whoever holds the link could set a password on an existing account by
        // "accepting" as them — an account takeover dressed as an invitation.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var existing = await SeedUserAsync().ConfigureAwait(false);

        var invite = await InviteAsync(team, existing.Email, TeamRole.Member).ConfigureAwait(false);

        var act = async () => await AcceptAsync(TokenOf(invite), $"Impostor {Guid.NewGuid():N}", "correct-horse-battery").ConfigureAwait(false);

        await act.ShouldThrowAsync<InvitationRequiresSignInException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_signed_in_account_cannot_accept_an_invitation_addressed_to_someone_else()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invited = await SeedUserAsync().ConfigureAwait(false);
        var bystander = await SeedUserAsync().ConfigureAwait(false);

        var invite = await InviteAsync(team, invited.Email, TeamRole.Member).ConfigureAwait(false);

        // A well-formed body — what the page sends once the bystander fills in the form it shows them.
        var act = async () => await AcceptAsAsync(TokenOf(invite), bystander.Id, $"Bystander {Guid.NewGuid():N}", "correct-horse-battery").ConfigureAwait(false);

        await act.ShouldThrowAsync<InvitationEmailMismatchException>().ConfigureAwait(false);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("  Admin ")]
    [InlineData("someone-elses-display-name")]
    public async Task An_address_without_an_at_sign_cannot_be_invited(string address)
    {
        // '@' is the line between addresses and display names. Inviting "admin" used to create an account at an address
        // that answered for the seeded admin's NAME — any account that can open a workspace could do it.
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = address, Role = TeamRole.Viewer }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvitationAddressNotAnEmailException>().ConfigureAwait(false);
        (await scope.Resolve<CodeSpaceDbContext>().TeamInvitation.AsNoTracking().AnyAsync(i => i.TeamId == team.TeamId).ConfigureAwait(false)).ShouldBeFalse("a refused address leaves no invitation behind");
    }

    [Fact]
    public async Task An_at_less_address_a_live_account_holds_can_be_invited_and_only_that_account_accepts()
    {
        // Earlier versions accepted any text as an address, and invitations are the only way into a team — so refusing
        // every '@'-less address would shut those accounts out of every team they are not already in. Inviting one is
        // safe: the address has an account, so accepting takes that account signed in and never creates another.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var legacy = await SeedUserAsync(email: $"Legacy{Guid.NewGuid():N}").ConfigureAwait(false);

        var invite = await InviteAsync(team, legacy.Email, TeamRole.Member).ConfigureAwait(false);

        var anonymous = async () => await AcceptAsync(TokenOf(invite), $"Impostor {Guid.NewGuid():N}", "correct-horse-battery").ConfigureAwait(false);
        await anonymous.ShouldThrowAsync<InvitationRequiresSignInException>().ConfigureAwait(false);

        var session = await AcceptAsAsync(TokenOf(invite), legacy.Id, name: null, password: null).ConfigureAwait(false);

        session.User.Id.ShouldBe(legacy.Id);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.TeamMembership.AsNoTracking().SingleAsync(m => m.TeamId == team.TeamId && m.UserId == legacy.Id).ConfigureAwait(false)).Role.ShouldBe(TeamRole.Member);
        (await db.User.AsNoTracking().CountAsync(u => u.DeletedDate == null && u.Email.ToLower() == legacy.Email.ToLower()).ConfigureAwait(false)).ShouldBe(1, "no second account at an address a live account holds");
    }

    public static TheoryData<string?, string?> MalformedNewAccounts => new()
    {
        { null, null },
        { "x", "x" },
        { "   ", "correct-horse-battery" },
        { "Maya", "too-short" },
        { "maya@home", "correct-horse-battery" },
    };

    [Theory]
    [MemberData(nameof(MalformedNewAccounts))]
    public async Task A_malformed_accept_answers_alike_whether_or_not_the_address_has_an_account(string? name, string? password)
    {
        // Anyone who can open a workspace can mint an invitation for an address of their choosing. The refusal of a body
        // no account could be created from used to differ — "sign in first" where the address had an account, "password
        // too short" where it had none — which answered whether the address was registered, with no side effect.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var registered = await SeedUserAsync().ConfigureAwait(false);
        var bystander = await SeedUserAsync().ConfigureAwait(false);
        var unregistered = $"nobody-{Guid.NewGuid():N}@x";
        var atRegistered = TokenOf(await InviteAsync(team, registered.Email, TeamRole.Viewer).ConfigureAwait(false));
        var atUnregistered = TokenOf(await InviteAsync(team, unregistered, TeamRole.Viewer).ConfigureAwait(false));

        foreach (Guid? caller in new Guid?[] { null, bystander.Id })
        {
            var registeredAnswer = await Record.ExceptionAsync(() => AcceptAsAsync(atRegistered, caller, name, password)).ConfigureAwait(false);
            var unregisteredAnswer = await Record.ExceptionAsync(() => AcceptAsAsync(atUnregistered, caller, name, password)).ConfigureAwait(false);

            var who = caller == null ? "anonymous" : "a signed-in bystander";
            registeredAnswer.ShouldNotBeNull($"{who}: a malformed accept must be refused");
            unregisteredAnswer.ShouldNotBeNull($"{who}: a malformed accept must be refused");
            unregisteredAnswer.GetType().ShouldBe(registeredAnswer.GetType(), $"{who}: the refusal must not depend on whether the address has an account");
            unregisteredAnswer.Message.ShouldBe(registeredAnswer.Message, $"{who}: the refusal must not depend on whether the address has an account");
        }

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().User.AsNoTracking().AnyAsync(u => u.Email == unregistered).ConfigureAwait(false)).ShouldBeFalse("nothing was created");
    }

    [Fact]
    public async Task A_taken_name_answers_alike_whether_or_not_the_address_has_an_account()
    {
        // The name is checked before the address, so a probe holding a name it knows is taken learns nothing about the
        // address from the refusal.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var registered = await SeedUserAsync().ConfigureAwait(false);
        var taken = await SeedUserAsync().ConfigureAwait(false);
        var atRegistered = TokenOf(await InviteAsync(team, registered.Email, TeamRole.Viewer).ConfigureAwait(false));
        var atUnregistered = TokenOf(await InviteAsync(team, $"nobody-{Guid.NewGuid():N}@x", TeamRole.Viewer).ConfigureAwait(false));

        var registeredAnswer = async () => await AcceptAsync(atRegistered, taken.Name, "correct-horse-battery").ConfigureAwait(false);
        var unregisteredAnswer = async () => await AcceptAsync(atUnregistered, taken.Name, "correct-horse-battery").ConfigureAwait(false);

        await registeredAnswer.ShouldThrowAsync<DisplayNameTakenException>().ConfigureAwait(false);
        await unregisteredAnswer.ShouldThrowAsync<DisplayNameTakenException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task The_invited_account_accepts_by_signing_in_first()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invited = await SeedUserAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, invited.Email, TeamRole.Admin).ConfigureAwait(false);

        using (var scope = _fixture.BeginScopeAs(invited.Id, teamId: null))
        {
            await scope.Resolve<IMediator>().Send(new AcceptInvitationCommand { Token = TokenOf(invite) }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var membership = await db.TeamMembership.SingleAsync(m => m.TeamId == team.TeamId && m.UserId == invited.Id).ConfigureAwait(false);

        membership.Role.ShouldBe(TeamRole.Admin);
    }

    [Fact]
    public async Task An_existing_member_cannot_be_invited_again()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var memberEmail = await db.User.Where(u => u.Id == team.Member).Select(u => u.Email).SingleAsync().ConfigureAwait(false);

        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = memberEmail, Role = TeamRole.Member }).ConfigureAwait(false);

        await act.ShouldThrowAsync<AlreadyTeamMemberException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_second_live_invitation_to_the_same_address_is_refused()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var email = $"twice-{Guid.NewGuid():N}@x";

        await InviteAsync(team, email, TeamRole.Member).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var act = async () => await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = email, Role = TeamRole.Member }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvitationAlreadyPendingException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task The_address_is_matched_regardless_of_how_it_was_typed()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var email = $"MiXeD-{Guid.NewGuid():N}@Example.COM";

        var invite = await InviteAsync(team, email, TeamRole.Member).ConfigureAwait(false);
        var preview = await PreviewAsync(TokenOf(invite)).ConfigureAwait(false);

        preview.Email.ShouldBe(email.ToLowerInvariant());
    }

    [Fact]
    public async Task The_listing_never_carries_a_token()
    {
        // A token readable from a list is a token every member of the team can redeem on someone
        // else's behalf. It exists once, in the reply to whoever created it.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"listed-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);
        var token = TokenOf(invite);

        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var listed = await scope.Resolve<IMediator>().Send(new ListTeamInvitationsQuery()).ConfigureAwait(false);

        var row = listed.ShouldHaveSingleItem();
        row.Id.ShouldBe(invite.InvitationId);
        System.Text.Json.JsonSerializer.Serialize(listed).ShouldNotContain(token);
    }

    [Fact]
    public async Task The_listing_names_who_sent_each_invitation()
    {
        // The reach rule lets an actor manage an invitation at their own role only when they sent it; the members screen
        // can hide what would be refused only if it knows the sender.
        var team = await SeedTeamAsync().ConfigureAwait(false);
        await InviteAsync(team, $"sent-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);
        var listed = await scope.Resolve<IMediator>().Send(new ListTeamInvitationsQuery()).ConfigureAwait(false);

        listed.ShouldHaveSingleItem().InvitedByUserId.ShouldBe(team.Owner);
    }

    [Fact]
    public async Task The_token_is_never_stored_in_a_form_that_could_be_replayed_from_the_database()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invite = await InviteAsync(team, $"hashed-{Guid.NewGuid():N}@x", TeamRole.Member).ConfigureAwait(false);
        var token = TokenOf(invite);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var stored = await db.TeamInvitation.AsNoTracking().SingleAsync(i => i.Id == invite.InvitationId).ConfigureAwait(false);

        stored.TokenHash.ShouldNotBe(token);
        stored.TokenHash.ShouldNotContain(token);
        stored.TokenHash.Length.ShouldBe(64, "a SHA-256 in hex is 64 characters — anything else means something other than a digest was persisted");
    }

    // ── Drivers ────────────────────────────────────────────────────────────────────

    private async Task<CreateInvitationResult> InviteAsync(SeededTeam team, string email, TeamRole role)
    {
        using var scope = _fixture.BeginScopeAs(team.Owner, team.TeamId);

        return await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = email, Role = role }).ConfigureAwait(false);
    }

    /// <summary>Anonymous, exactly as the invitee arrives: no user, no team header.</summary>
    private async Task<InvitationPreview> PreviewAsync(string token)
    {
        using var scope = _fixture.BeginScopeAs(userId: null, teamId: null);

        return await scope.Resolve<IMediator>().Send(new PreviewInvitationQuery { Token = token }).ConfigureAwait(false);
    }

    private async Task<InvitationPreview> PreviewAsAsync(string token, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId: null);

        return await scope.Resolve<IMediator>().Send(new PreviewInvitationQuery { Token = token }).ConfigureAwait(false);
    }

    private async Task ManageAsync<TResponse>(Guid actor, Guid teamId, IRequest<TResponse> command, params string[] roles)
    {
        using var scope = _fixture.BeginScopeAs(actor, teamId, roles);

        await scope.Resolve<IMediator>().Send(command).ConfigureAwait(false);
    }

    /// <summary>Straight to the row, so the test sets exactly who sent it — the rule under test reads that column.</summary>
    private async Task<Guid> SeedPendingInvitationAsync(Guid teamId, TeamRole role, Guid sentBy)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var invitation = new TeamInvitation { Id = Guid.NewGuid(), TeamId = teamId, Email = $"pending-{Guid.NewGuid():N}@x", Role = role, TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), Status = InvitationStatus.Pending, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), InvitedByUserId = sentBy };

        db.TeamInvitation.Add(invitation);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return invitation.Id;
    }

    private async Task<TeamInvitation> LoadInvitationAsync(Guid invitationId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().TeamInvitation.AsNoTracking().SingleAsync(i => i.Id == invitationId).ConfigureAwait(false);
    }

    private async Task<Guid> SeedMemberAsync(Guid teamId, TeamRole role)
    {
        var user = await SeedUserAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync().ConfigureAwait(false);

        return user.Id;
    }

    private async Task<Messages.Commands.Auth.SignInResponse> AcceptAsync(string token, string name, string password) =>
        await AcceptAsAsync(token, userId: null, name, password).ConfigureAwait(false);

    private async Task<Messages.Commands.Auth.SignInResponse> AcceptAsAsync(string token, Guid? userId, string? name, string? password)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId: null);

        return await scope.Resolve<IMediator>().Send(new AcceptInvitationCommand { Token = token, Name = name, Password = password }).ConfigureAwait(false);
    }

    private async Task ExpireAsync(Guid invitationId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var invitation = await db.TeamInvitation.SingleAsync(i => i.Id == invitationId).ConfigureAwait(false);

        invitation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static string TokenOf(CreateInvitationResult invite) => invite.InviteUrl[(invite.InviteUrl.LastIndexOf('/') + 1)..];

    private async Task<User> SeedUserAsync(string? email = null, string? name = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = email ?? $"existing-{Guid.NewGuid():N}@x", Name = name ?? $"Existing {Guid.NewGuid():N}" };

        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return user;
    }

    private async Task<SeededTeam> SeedTeamAsync(TeamKind kind = TeamKind.Workspace)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"own-{suffix}@x", Name = "owner" };
        var admin = new User { Id = Guid.NewGuid(), Email = $"adm-{suffix}@x", Name = $"admin-{suffix}" };
        var member = new User { Id = Guid.NewGuid(), Email = $"mem-{suffix}@x", Name = "member" };
        var viewer = new User { Id = Guid.NewGuid(), Email = $"vie-{suffix}@x", Name = "viewer" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"inv-{suffix}", Name = "Invitees", Kind = kind };

        db.User.AddRange(owner, admin, member, viewer);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, owner.Id));
        db.TeamMembership.AddRange(
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
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unseeded role"),
        };
    }
}
