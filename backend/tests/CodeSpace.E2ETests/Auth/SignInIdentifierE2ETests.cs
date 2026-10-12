using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline, real password hashing, Postgres): what the sign-in identifier resolves to.
///
/// <para>The lockout this pins: sign-in matched the identifier against email OR display name and took the first
/// account by id. Display names were free text, so any account able to open a workspace could invite throwaway
/// addresses, accept them anonymously under the name <c>victim@corp</c>, and within a couple of tries hold an
/// account that sorted before the victim's. From then on the victim's correct password answered 401 — and the
/// attacker's password signed in AS the shadow under the victim's address. There is no self-service reset, so it
/// took an instance admin to undo.</para>
///
/// <para>The fix keeps the two identifier spaces apart, and every half is pinned: an identifier with '@' resolves only
/// as an address and one without it as a display name (an '@'-less address an earlier version let in is tried only
/// when no account carries the name); a name signs in only when exactly one live account carries it; no account can
/// be created with an email-shaped name or a name that is already somebody's address or name; and no invitation can
/// be addressed without '@' — which is how any workspace owner took the seeded admin's "admin" sign-in — unless a live
/// account already holds that address, so the accounts an earlier version created at one can still join a team.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class SignInIdentifierE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string VictimPassword = "victim-password-123";
    private const string AttackerPassword = "attacker-password-123";
    private const string SeedAdminPassword = "changeme123";

    private readonly TenantHarness _tenants;

    public SignInIdentifierE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Fact]
    public async Task An_account_cannot_be_created_under_a_name_that_is_another_accounts_email()
    {
        var victimEmail = await SeedVictimAsync();
        var token = await InviteFromAttackerWorkspaceAsync();

        var accept = await AcceptAsync(token, name: victimEmail);

        accept.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(accept));
        (await CodeOfAsync(accept)).ShouldBe("display_name_looks_like_email");
        (await SignInAsync(victimEmail, VictimPassword)).StatusCode.ShouldBe(HttpStatusCode.OK, "the victim still signs in");
    }

    [Fact]
    public async Task An_account_cannot_be_created_under_another_accounts_display_name()
    {
        var victimName = $"Victim {Guid.NewGuid():N}";
        await SeedVictimAsync(victimName);
        var token = await InviteFromAttackerWorkspaceAsync();

        var accept = await AcceptAsync(token, name: victimName.ToUpperInvariant());

        accept.StatusCode.ShouldBe(HttpStatusCode.Conflict, await TenantHarness.DescribeAsync(accept));
        (await CodeOfAsync(accept)).ShouldBe("display_name_taken");
        (await SignInAsync(victimName, VictimPassword)).StatusCode.ShouldBe(HttpStatusCode.OK, "the victim's name still signs them in — it is still theirs alone");
    }

    [Fact]
    public async Task An_address_resolves_to_its_own_account_even_when_an_earlier_account_is_named_with_it()
    {
        // A shadow left over from before the fix: named with the victim's address, and given an id that sorts first
        // — exactly the row the old "first match by id" lookup returned.
        var victimEmail = await SeedVictimAsync();
        await _tenants.AccountAsync(name: victimEmail, password: AttackerPassword, id: Guid.Parse("00000000-0000-0000-0000-0000000000a1"));

        var victim = await SignInAsync(victimEmail.ToUpperInvariant(), VictimPassword);
        var shadow = await SignInAsync(victimEmail, AttackerPassword);

        victim.StatusCode.ShouldBe(HttpStatusCode.OK, $"the address is the victim's — {await TenantHarness.DescribeAsync(victim)}");
        (await SignedInEmailAsync(victim)).ShouldBe(victimEmail);
        shadow.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the shadow's password must never open a session under the victim's address");
    }

    [Fact]
    public async Task A_display_name_two_accounts_share_signs_in_neither()
    {
        var shared = $"Shared {Guid.NewGuid():N}";
        var firstEmail = $"first-{Guid.NewGuid():N}@tenant.test";
        await _tenants.AccountAsync(email: firstEmail, name: shared, password: VictimPassword);
        await _tenants.AccountAsync(name: shared, password: AttackerPassword);

        (await SignInAsync(shared, VictimPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "an ambiguous name names nobody");
        (await SignInAsync(shared, AttackerPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "an ambiguous name names nobody");
        (await SignInAsync(firstEmail, VictimPassword)).StatusCode.ShouldBe(HttpStatusCode.OK, "the address still signs its owner in");
    }

    [Fact]
    public async Task A_display_name_only_one_account_carries_still_signs_in()
    {
        var name = $"Solo {Guid.NewGuid():N}";
        await SeedVictimAsync(name);

        var response = await SignInAsync(name.ToLowerInvariant(), VictimPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData(" admin ")]
    public async Task An_address_without_an_at_sign_cannot_be_invited_so_the_seeded_admin_keeps_its_name(string address)
    {
        // Sign-in tried the address before the name, and invitations accepted any text as an address — so any account
        // that can open a workspace invited "Admin", accepted it anonymously, and from then on "admin" signed the
        // attacker in and refused the seeded admin's own password.
        (await SignInAsync("admin", SeedAdminPassword)).StatusCode.ShouldBe(HttpStatusCode.OK, "precondition: the seeded admin signs in by name");

        var invite = await InviteFromAttackerWorkspaceAsync(address);

        invite.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(invite));
        (await CodeOfAsync(invite)).ShouldBe("invitation_address_not_an_email");

        var seedAdmin = await SignInAsync("admin", SeedAdminPassword);
        seedAdmin.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(seedAdmin));
        (await SignedInEmailAsync(seedAdmin)).ShouldBe("admin@codespace.local");
    }

    [Fact]
    public async Task Another_accounts_display_name_cannot_be_invited_as_an_address()
    {
        var victimName = $"victimname{Guid.NewGuid():N}";
        await SeedVictimAsync(victimName);

        var invite = await InviteFromAttackerWorkspaceAsync(victimName);

        invite.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(invite));
        (await SignInAsync(victimName, VictimPassword)).StatusCode.ShouldBe(HttpStatusCode.OK, "the name still signs its holder in");
    }

    [Fact]
    public async Task A_display_name_wins_over_an_at_less_address_an_earlier_version_let_in()
    {
        // The account an attack on an earlier version left behind: invited at an address spelled like the victim's
        // name — "admin" was the one that mattered — and accepted. Its address must not answer for the victim's name.
        // Not "admin" itself: this class shares one database, and an account holding that address would make it
        // invitable again for the test that pins the seeded admin's case.
        var victimName = $"victimname{Guid.NewGuid():N}";
        var victimEmail = await SeedVictimAsync(victimName);
        await _tenants.AccountAsync(email: victimName, password: AttackerPassword);

        var victim = await SignInAsync(victimName, VictimPassword);
        var shadow = await SignInAsync(victimName, AttackerPassword);

        victim.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(victim));
        (await SignedInEmailAsync(victim)).ShouldBe(victimEmail);
        shadow.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "an '@'-less address never takes a name's sign-in from its holder");
    }

    [Fact]
    public async Task An_at_less_address_no_display_name_claims_still_signs_its_account_in()
    {
        // Not breaking the accounts an earlier version created at such an address: with no name in the way it is still
        // an address, and it still signs in.
        var legacyAddress = $"legacy-{Guid.NewGuid():N}";
        await _tenants.AccountAsync(email: legacyAddress, password: VictimPassword);

        var response = await SignInAsync(legacyAddress, VictimPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));
        (await SignedInEmailAsync(response)).ShouldBe(legacyAddress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // typed in another letter case — invitations store the address lower-cased
    public async Task An_account_at_an_at_less_address_can_still_be_invited_and_only_it_can_accept(bool upperCase)
    {
        // Invitations are the only way into a team, so refusing every '@'-less address shut the accounts an earlier
        // version created at one out of every team they were not already in. Inviting one is safe: the address has an
        // account, so accepting takes that account signed in, and no new account can ever be created there.
        var legacyAddress = $"legacy{Guid.NewGuid():N}";
        var legacy = await _tenants.AccountAsync(email: legacyAddress, password: VictimPassword);
        var owner = await _tenants.AccountAsync();
        var workspace = await _tenants.TeamAsync((owner, TeamRole.Owner));

        var invite = await _tenants.SendAsync(HttpMethod.Post, "/api/teams/invitations", owner, workspace, new { email = upperCase ? legacyAddress.ToUpperInvariant() : legacyAddress, role = nameof(TeamRole.Member) });
        invite.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(invite));
        var token = Uri.UnescapeDataString(JsonDocument.Parse(await invite.Content.ReadAsStringAsync()).RootElement.GetProperty("inviteUrl").GetString()!.Split('/').Last());

        var anonymous = await AcceptAsync(token, name: $"Impostor {Guid.NewGuid():N}");
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await TenantHarness.DescribeAsync(anonymous));
        (await CodeOfAsync(anonymous)).ShouldBe("invitation_requires_sign_in");
        (await _tenants.DbAsync(db => db.User.CountAsync(u => u.DeletedDate == null && u.Email.ToLower() == legacyAddress))).ShouldBe(1, "no second account at an address a live account holds");

        var accepted = await _tenants.SendAsync(HttpMethod.Post, $"/api/invitations/{token}/accept", legacy, teamId: null, new { });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(accepted));
        (await _tenants.DbAsync(db => db.TeamMembership.Where(m => m.TeamId == workspace && m.UserId == legacy).Select(m => m.Role).SingleAsync())).ShouldBe(TeamRole.Member);
    }

    [Fact]
    public async Task An_identifier_with_an_at_sign_never_resolves_a_display_name()
    {
        // A name shaped like an address, left from before names refused '@', answers for no address — not even one no
        // account holds yet, which the next invitation could otherwise make ambiguous or capture.
        var addressShapedName = $"someone-{Guid.NewGuid():N}@nowhere.test";
        await _tenants.AccountAsync(name: addressShapedName, password: AttackerPassword);

        (await SignInAsync(addressShapedName, AttackerPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private async Task<string> SeedVictimAsync(string? name = null)
    {
        var email = $"victim-{Guid.NewGuid():N}@other-corp.test";
        var victim = await _tenants.AccountAsync(email: email, name: name, password: VictimPassword);
        await _tenants.TeamAsync((victim, TeamRole.Owner));

        return email;
    }

    /// <summary>The attacker owns a workspace of their own — every account may open one — and invites a throwaway address into it.</summary>
    private async Task<string> InviteFromAttackerWorkspaceAsync()
    {
        var invite = await InviteFromAttackerWorkspaceAsync($"shadow-{Guid.NewGuid():N}@evil.test");
        invite.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(invite));

        return Uri.UnescapeDataString(JsonDocument.Parse(await invite.Content.ReadAsStringAsync()).RootElement.GetProperty("inviteUrl").GetString()!.Split('/').Last());
    }

    private async Task<HttpResponseMessage> InviteFromAttackerWorkspaceAsync(string address)
    {
        var attacker = await _tenants.AccountAsync();
        var workspace = await _tenants.TeamAsync((attacker, TeamRole.Owner));

        return await _tenants.SendAsync(HttpMethod.Post, "/api/teams/invitations", attacker, workspace, new { email = address, role = nameof(TeamRole.Viewer) });
    }

    private async Task<HttpResponseMessage> AcceptAsync(string token, string name) =>
        await _tenants.Client().PostAsJsonAsync($"/api/invitations/{token}/accept", new { name, password = AttackerPassword });

    private async Task<HttpResponseMessage> SignInAsync(string identifier, string password) =>
        await _tenants.Client().PostAsJsonAsync("/api/auth/sign-in", new { name = identifier, password });

    private static async Task<string?> SignedInEmailAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("user").GetProperty("email").GetString();

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
}
