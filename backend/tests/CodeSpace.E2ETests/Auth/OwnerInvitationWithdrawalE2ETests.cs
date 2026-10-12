using System.Net;
using System.Text.Json;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline, JWT auth, mediator behaviours, Postgres): a pending Owner invitation always
/// has somebody who can withdraw it.
///
/// <para>A leaked Owner link is a seat at the top of the team for whoever holds it, for the link's whole lifetime.
/// When regenerate and revoke shared the member reach rule, an Owner invitation was in reach of its sender alone, and
/// only while the sender was still an Owner: a co-Owner and the instance admin were refused, and once the sender left
/// or handed the team on nobody could kill the link. Revoking raises nobody, so it now reaches every invitation up to
/// the caller's own rank; an Owner, and the instance admin, reach every invitation in the team. Regenerating still
/// follows the reach rule below Owner — <c>InvitationReachE2ETests</c> pins the Admin who cannot.</para>
///
/// <para>Its own class, so its own host: every anonymous preview here spends from the per-caller budget of
/// <c>AnonymousRateLimitExtension.PermitsPerWindow</c> per minute, which a test host shares across its class.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class OwnerInvitationWithdrawalE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public OwnerInvitationWithdrawalE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Theory]
    [InlineData("regenerate")]
    [InlineData("revoke")]
    public async Task A_co_owner_manages_another_owners_owner_invitation(string action)
    {
        var team = await SeedTeamAsync();
        var coOwner = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(team.TeamId, coOwner, TeamRole.Owner);
        var invitation = await InviteAsync(team.Owner, team.TeamId, TeamRole.Owner);

        var response = await ManageAsync(action, invitation.Id, coOwner, team.TeamId);

        response.IsSuccessStatusCode.ShouldBeTrue($"nobody ranks above an Owner to arbitrate, and an Owner may invite at Owner outright — {await TenantHarness.DescribeAsync(response)}");
        (await PreviewStatusAsync(invitation.Token)).ShouldBe(HttpStatusCode.NotFound, "either way the original link is dead");
    }

    [Theory]
    [InlineData(false)]   // a co-Owner
    [InlineData(true)]    // the instance admin, who is no member at all
    public async Task An_owner_invitation_stays_revocable_after_its_sender_leaves(bool byInstanceAdmin)
    {
        var team = await SeedTeamAsync();
        var coOwner = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(team.TeamId, coOwner, TeamRole.Owner);
        var invitation = await InviteAsync(team.Owner, team.TeamId, TeamRole.Owner);

        var leave = await _tenants.SendAsync(HttpMethod.Post, "/api/teams/members/leave", team.Owner, team.TeamId);
        leave.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(leave));

        var actor = byInstanceAdmin ? await InstanceAdminAsync() : coOwner;
        var revoke = await ManageAsync("revoke", invitation.Id, actor, team.TeamId);

        revoke.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(revoke));
        (await PreviewStatusAsync(invitation.Token)).ShouldBe(HttpStatusCode.NotFound, "the departed Owner's link no longer makes anyone an Owner");
    }

    [Theory]
    [InlineData(false)]   // the new Owner
    [InlineData(true)]    // the demoted sender: withdrawing what you sent raises nobody, as it never did
    public async Task After_a_transfer_the_old_owners_owner_invitation_can_be_withdrawn_but_not_reissued_by_them(bool bySender)
    {
        var team = await SeedTeamAsync();
        var successor = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(team.TeamId, successor, TeamRole.Member);
        var invitation = await InviteAsync(team.Owner, team.TeamId, TeamRole.Owner);

        var transfer = await _tenants.SendAsync(HttpMethod.Post, "/api/teams/transfer-ownership", team.Owner, team.TeamId, new { toUserId = successor });
        transfer.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(transfer));
        (await RoleOfAsync(team.TeamId, team.Owner)).ShouldBe(TeamRole.Admin, "precondition: the transfer demoted the old Owner");

        var reissue = await ManageAsync("regenerate", invitation.Id, team.Owner, team.TeamId);
        reissue.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"their own invitation, but above them now — re-issuing it would be a way back to Owner: {await TenantHarness.DescribeAsync(reissue)}");
        (await CodeOfAsync(reissue)).ShouldBe("invitation_outranks_actor");

        var revoke = await ManageAsync("revoke", invitation.Id, bySender ? team.Owner : successor, team.TeamId);
        revoke.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(revoke));
        (await PreviewStatusAsync(invitation.Token)).ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record SeededTeam(Guid TeamId, Guid Owner);

    private sealed record SentInvitation(Guid Id, string Token);

    private async Task<SeededTeam> SeedTeamAsync()
    {
        var owner = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((owner, TeamRole.Owner));

        return new SeededTeam(teamId, owner);
    }

    private async Task<SentInvitation> InviteAsync(Guid inviter, Guid teamId, TeamRole role)
    {
        var response = await _tenants.SendAsync(HttpMethod.Post, "/api/teams/invitations", inviter, teamId, new { email = $"invitee-{Guid.NewGuid():N}@tenant.test", role = role.ToString() });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var token = Uri.UnescapeDataString(body.GetProperty("inviteUrl").GetString()!.Split('/').Last());

        return new SentInvitation(body.GetProperty("invitationId").GetGuid(), token);
    }

    private async Task<HttpResponseMessage> ManageAsync(string action, Guid invitationId, Guid actor, Guid teamId) => action == "regenerate"
        ? await _tenants.SendAsync(HttpMethod.Post, $"/api/teams/invitations/{invitationId}/regenerate", actor, teamId)
        : await _tenants.SendAsync(HttpMethod.Delete, $"/api/teams/invitations/{invitationId}", actor, teamId);

    private async Task<Guid> InstanceAdminAsync()
    {
        var admin = await _tenants.AccountAsync();
        await _tenants.MakeGlobalAdminAsync(admin);

        return admin;
    }

    private async Task<HttpStatusCode> PreviewStatusAsync(string token) =>
        (await _tenants.SendAsync(HttpMethod.Get, $"/api/invitations/{token}", userId: null, teamId: null)).StatusCode;

    private async Task<TeamRole> RoleOfAsync(Guid teamId, Guid userId) =>
        await _tenants.DbAsync(db => db.TeamMembership.AsNoTracking().Where(m => m.TeamId == teamId && m.UserId == userId).Select(m => m.Role).SingleAsync());

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
}
