using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Invitations;
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

        var act = async () => await AcceptAsync(TokenOf(invite), "Impostor", "correct-horse-battery").ConfigureAwait(false);

        await act.ShouldThrowAsync<InvitationRequiresSignInException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_signed_in_account_cannot_accept_an_invitation_addressed_to_someone_else()
    {
        var team = await SeedTeamAsync().ConfigureAwait(false);
        var invited = await SeedUserAsync().ConfigureAwait(false);
        var bystander = await SeedUserAsync().ConfigureAwait(false);

        var invite = await InviteAsync(team, invited.Email, TeamRole.Member).ConfigureAwait(false);

        using var scope = _fixture.BeginScopeAs(bystander.Id, teamId: null);
        var act = async () => await scope.Resolve<IMediator>().Send(new AcceptInvitationCommand { Token = TokenOf(invite) }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvitationEmailMismatchException>().ConfigureAwait(false);
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

    private async Task<Messages.Commands.Auth.SignInResponse> AcceptAsync(string token, string name, string password)
    {
        using var scope = _fixture.BeginScopeAs(userId: null, teamId: null);

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

    private async Task<User> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"existing-{Guid.NewGuid():N}@x", Name = "Existing" };

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
        var admin = new User { Id = Guid.NewGuid(), Email = $"adm-{suffix}@x", Name = "admin" };
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
