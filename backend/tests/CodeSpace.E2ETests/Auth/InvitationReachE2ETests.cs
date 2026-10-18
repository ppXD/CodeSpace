using System.Net;
using System.Text.Json;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline, JWT auth, mediator behaviours, Postgres): an invitation carries a role, so
/// managing one is acting at that role — the same reach rule that governs acting on a member.
///
/// <para>The escalation this pins: an Admin listed a pending Owner-role invitation, regenerated it (which hands the
/// regenerator a fresh working link and kills the inviter's), accepted it with a puppet account, and had the puppet
/// promote them to Owner. Creating an Owner invitation was refused to an Admin; regenerating one was not. Regenerating
/// a PEER Admin's invitation crossed the "never across your own rank" rule the same way.</para>
///
/// <para>Revoking raises nobody, so it reaches every invitation up to the caller's own rank. The Owner-invitation
/// cases — a co-Owner's, a departed or demoted sender's — are in <c>OwnerInvitationWithdrawalE2ETests</c>, which
/// has an anonymous request budget of its own.</para>
///
/// <para>Also pins that the anonymous preview no longer says whether the invited address has an account — any
/// account that can open a workspace could invite an arbitrary address and read the answer.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class InvitationReachE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public InvitationReachE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Fact]
    public async Task An_admin_cannot_regenerate_an_owner_invitation_and_climb_to_owner()
    {
        var team = await SeedTeamAsync();
        var invitation = await InviteAsync(team.Owner, team.TeamId, TeamRole.Owner);

        var regenerate = await _tenants.SendAsync(HttpMethod.Post, $"/api/teams/invitations/{invitation.Id}/regenerate", team.Admin, team.TeamId);

        regenerate.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(regenerate));
        (await CodeOfAsync(regenerate)).ShouldBe("invitation_outranks_actor");

        var original = await _tenants.SendAsync(HttpMethod.Get, $"/api/invitations/{invitation.Token}", userId: null, teamId: null);
        original.StatusCode.ShouldBe(HttpStatusCode.OK, "a refused regenerate must rotate nothing — the Owner's own link still works");

        (await RoleOfAsync(team.TeamId, team.Admin)).ShouldBe(TeamRole.Admin);
    }

    [Fact]
    public async Task An_admin_cannot_revoke_an_owner_invitation()
    {
        var team = await SeedTeamAsync();
        var invitation = await InviteAsync(team.Owner, team.TeamId, TeamRole.Owner);

        var revoke = await _tenants.SendAsync(HttpMethod.Delete, $"/api/teams/invitations/{invitation.Id}", team.Admin, team.TeamId);

        revoke.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(revoke));
        (await CodeOfAsync(revoke)).ShouldBe("invitation_outranks_actor");

        var original = await _tenants.SendAsync(HttpMethod.Get, $"/api/invitations/{invitation.Token}", userId: null, teamId: null);
        original.StatusCode.ShouldBe(HttpStatusCode.OK, "the Owner's invitation is still pending");
    }

    [Theory]
    [InlineData("regenerate", false)]   // a working link to a peer's grant — acting across their rank
    [InlineData("revoke", true)]        // withdrawing a grant raises nobody
    public async Task An_admin_may_revoke_but_not_regenerate_a_peer_admins_admin_invitation(string action, bool allowed)
    {
        var team = await SeedTeamAsync();
        var peer = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(team.TeamId, peer, TeamRole.Admin);
        var invitation = await InviteAsync(peer, team.TeamId, TeamRole.Admin);

        var response = await ManageAsync(action, invitation.Id, team.Admin, team.TeamId);

        if (allowed)
        {
            response.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(response));
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"rank is never reached ACROSS to re-issue: an Admin regenerating another Admin's Admin invitation — {await TenantHarness.DescribeAsync(response)}");
        (await CodeOfAsync(response)).ShouldBe("invitation_outranks_actor");
    }

    [Theory]
    [InlineData("regenerate")]
    [InlineData("revoke")]
    public async Task An_admin_manages_the_admin_invitation_they_sent(string action)
    {
        var team = await SeedTeamAsync();
        var invitation = await InviteAsync(team.Admin, team.TeamId, TeamRole.Admin);

        var response = await ManageAsync(action, invitation.Id, team.Admin, team.TeamId);

        response.IsSuccessStatusCode.ShouldBeTrue($"at your own rank, your own invitation is yours to rotate or withdraw — {await TenantHarness.DescribeAsync(response)}");
    }

    [Theory]
    [InlineData("regenerate")]
    [InlineData("revoke")]
    public async Task The_owner_manages_an_invitation_below_their_rank_that_someone_else_sent(string action)
    {
        var team = await SeedTeamAsync();
        var invitation = await InviteAsync(team.Admin, team.TeamId, TeamRole.Member);

        var response = await ManageAsync(action, invitation.Id, team.Owner, team.TeamId);

        response.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(response));
    }

    [Fact]
    public async Task The_anonymous_preview_reads_the_same_whether_or_not_the_address_has_an_account()
    {
        var team = await SeedTeamAsync();
        var existingEmail = $"existing-{Guid.NewGuid():N}@other-tenant.test";
        await _tenants.AccountAsync(email: existingEmail);

        var forExisting = await InviteAsync(team.Owner, team.TeamId, TeamRole.Viewer, existingEmail);
        var forNobody = await InviteAsync(team.Owner, team.TeamId, TeamRole.Viewer, $"nobody-{Guid.NewGuid():N}@other-tenant.test");

        var existingPreview = await PreviewAsync(forExisting.Token, userId: null);
        var nobodyPreview = await PreviewAsync(forNobody.Token, userId: null);

        // accountExists is only the old spelling of forSignedInAccount, kept for the invite page that shipped before it. Were it
        // ever the address's own fact again, this is the assertion that goes red: an existence oracle for any address.
        existingPreview.GetProperty("accountExists").GetBoolean().ShouldBeFalse("an anonymous preview must not say whether the address has an account — it is an existence oracle for any address");
        nobodyPreview.GetProperty("accountExists").GetBoolean().ShouldBeFalse();
        PropertyNames(existingPreview).ShouldBe(PropertyNames(nobodyPreview), "the two previews must have the same shape");
        existingPreview.GetProperty("forSignedInAccount").GetBoolean().ShouldBeFalse();
        nobodyPreview.GetProperty("forSignedInAccount").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_invited_account_signed_in_is_told_the_invitation_is_its_own()
    {
        var team = await SeedTeamAsync();
        var inviteeEmail = $"invitee-{Guid.NewGuid():N}@tenant.test";
        var invitee = await _tenants.AccountAsync(email: inviteeEmail);
        var bystander = await _tenants.AccountAsync();
        var invitation = await InviteAsync(team.Owner, team.TeamId, TeamRole.Member, inviteeEmail);

        (await PreviewAsync(invitation.Token, invitee)).GetProperty("forSignedInAccount").GetBoolean().ShouldBeTrue("the hint is about the caller themselves, so it may be given to the caller");
        (await PreviewAsync(invitation.Token, bystander)).GetProperty("forSignedInAccount").GetBoolean().ShouldBeFalse("a different signed-in account learns nothing about the invited address");
    }

    [Fact]
    public async Task The_old_spelling_the_previous_invite_page_reads_says_the_same_thing_to_the_same_caller()
    {
        // The API is replaced before the SPA, so the page that shipped before forSignedInAccount reads accountExists from this
        // response: without it, a signed-in invitee is asked to "Create your account" for an address that already has one.
        var team = await SeedTeamAsync();
        var inviteeEmail = $"invitee-{Guid.NewGuid():N}@tenant.test";
        var invitee = await _tenants.AccountAsync(email: inviteeEmail);
        var bystander = await _tenants.AccountAsync();
        var invitation = await InviteAsync(team.Owner, team.TeamId, TeamRole.Member, inviteeEmail);

        (await PreviewAsync(invitation.Token, invitee)).GetProperty("accountExists").GetBoolean().ShouldBeTrue("the signed-in invitee is who the older page was written for");
        (await PreviewAsync(invitation.Token, bystander)).GetProperty("accountExists").GetBoolean().ShouldBeFalse("a different signed-in account learns nothing about the invited address");
        (await PreviewAsync(invitation.Token, userId: null)).GetProperty("accountExists").GetBoolean().ShouldBeFalse("nor does a stranger");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record SeededTeam(Guid TeamId, Guid Owner, Guid Admin);

    private sealed record SentInvitation(Guid Id, string Token);

    private async Task<SeededTeam> SeedTeamAsync()
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin));

        return new SeededTeam(teamId, owner, admin);
    }

    private async Task<SentInvitation> InviteAsync(Guid inviter, Guid teamId, TeamRole role, string? email = null)
    {
        var response = await _tenants.SendAsync(HttpMethod.Post, "/api/teams/invitations", inviter, teamId, new { email = email ?? $"invitee-{Guid.NewGuid():N}@tenant.test", role = role.ToString() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var token = Uri.UnescapeDataString(body.GetProperty("inviteUrl").GetString()!.Split('/').Last());

        return new SentInvitation(body.GetProperty("invitationId").GetGuid(), token);
    }

    private async Task<HttpResponseMessage> ManageAsync(string action, Guid invitationId, Guid actor, Guid teamId) => action == "regenerate"
        ? await _tenants.SendAsync(HttpMethod.Post, $"/api/teams/invitations/{invitationId}/regenerate", actor, teamId)
        : await _tenants.SendAsync(HttpMethod.Delete, $"/api/teams/invitations/{invitationId}", actor, teamId);

    private async Task<JsonElement> PreviewAsync(string token, Guid? userId)
    {
        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/invitations/{token}", userId, teamId: null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<TeamRole> RoleOfAsync(Guid teamId, Guid userId) =>
        await _tenants.DbAsync(db => db.TeamMembership.AsNoTracking().Where(m => m.TeamId == teamId && m.UserId == userId).Select(m => m.Role).SingleAsync());

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();

    private static string[] PropertyNames(JsonElement element) => element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
}
