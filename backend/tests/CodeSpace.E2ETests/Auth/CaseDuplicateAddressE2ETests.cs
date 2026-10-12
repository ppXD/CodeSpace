using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline, real password hashing, Postgres): a deployment that already held two live
/// accounts at one address in different letter case before migration 0245 — which then creates no index and warns
/// instead of refusing to start.
///
/// <para>The regression this pins: sign-in and invitations assumed an address names one row, so on such a deployment
/// both accounts got 400 "Sequence contains more than one element" for their own correct password — the raw exception
/// text on the wire — and so did every invitation to the address. Each account must keep signing in with its own
/// spelling, a spelling that matches neither exactly must sign in nobody, and the address must stay invitable without
/// a third account ever being created at it.</para>
///
/// <para>This class's factory owns its database, so it drops <c>ux_app_user_email_lower</c> there to stand in for that
/// deployment.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class CaseDuplicateAddressE2ETests : IClassFixture<TaskLaunchApiFactory>, IAsyncLifetime
{
    private const string UpperPassword = "upper-password-123";
    private const string LowerPassword = "lower-password-123";

    private readonly TenantHarness _tenants;

    public CaseDuplicateAddressE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    public async Task InitializeAsync() => await _tenants.DbAsync(db => db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS ux_app_user_email_lower"));

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Each_account_signs_in_with_its_own_exact_address_and_a_third_spelling_signs_in_nobody()
    {
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "dup.{0}@corp.test");

        var upper = await SignInAsync(pair.UpperEmail, UpperPassword);
        var lower = await SignInAsync(pair.LowerEmail, LowerPassword);
        var third = await SignInAsync(pair.LowerEmail.ToUpperInvariant(), LowerPassword);

        upper.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(upper));
        (await SignedInIdAsync(upper)).ShouldBe(pair.Upper);
        lower.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(lower));
        (await SignedInIdAsync(lower)).ShouldBe(pair.Lower);
        third.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"a spelling that matches neither exactly is ambiguous and names nobody — {await TenantHarness.DescribeAsync(third)}");
        (await third.Content.ReadAsStringAsync()).ShouldNotContain("Sequence contains", Case.Insensitive);
    }

    [Fact]
    public async Task The_address_stays_invitable_and_a_signed_in_holder_accepts_while_an_anonymous_accept_creates_nothing()
    {
        // Invitations store the address lower-cased; neither account is spelled that way, so the address is ambiguous.
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "DUP.{0}@corp.TEST");
        var owner = await _tenants.AccountAsync();
        var workspace = await _tenants.TeamAsync((owner, TeamRole.Owner));

        var invite = await InviteAsync(owner, workspace, pair.LowerEmail);
        invite.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(invite));
        var token = await TokenOfAsync(invite);

        var anonymous = await _tenants.Client().PostAsJsonAsync($"/api/invitations/{token}/accept", new { name = $"Third {Guid.NewGuid():N}", password = "correct-horse-battery" });
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await TenantHarness.DescribeAsync(anonymous));
        (await _tenants.DbAsync(db => db.User.CountAsync(u => u.DeletedDate == null && u.Email.ToLower() == pair.LowerEmail.ToLower()))).ShouldBe(2, "no third account at an address two accounts already hold");

        var accepted = await _tenants.SendAsync(HttpMethod.Post, $"/api/invitations/{token}/accept", pair.Upper, teamId: null, new { });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(accepted));
        (await _tenants.DbAsync(db => db.TeamMembership.AnyAsync(m => m.TeamId == workspace && m.UserId == pair.Upper))).ShouldBeTrue();
    }

    [Fact]
    public async Task Inviting_the_exact_spelling_of_one_account_of_the_pair_does_not_throw()
    {
        var pair = await SeedPairAsync("Dup.{0}@Corp.test", "dup.{0}@corp.test");
        var owner = await _tenants.AccountAsync();
        var workspace = await _tenants.TeamAsync((owner, TeamRole.Owner));

        var invite = await InviteAsync(owner, workspace, pair.LowerEmail);

        invite.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(invite));
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record Pair(Guid Upper, string UpperEmail, Guid Lower, string LowerEmail);

    private async Task<Pair> SeedPairAsync(string upperShape, string lowerShape)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var upperEmail = string.Format(upperShape, suffix);
        var lowerEmail = string.Format(lowerShape, suffix);

        var upper = await _tenants.AccountAsync(email: upperEmail, password: UpperPassword);
        var lower = await _tenants.AccountAsync(email: lowerEmail, password: LowerPassword);

        return new Pair(upper, upperEmail, lower, lowerEmail);
    }

    private async Task<HttpResponseMessage> InviteAsync(Guid owner, Guid workspace, string address) =>
        await _tenants.SendAsync(HttpMethod.Post, "/api/teams/invitations", owner, workspace, new { email = address, role = nameof(TeamRole.Member) });

    private async Task<HttpResponseMessage> SignInAsync(string identifier, string password) =>
        await _tenants.Client().PostAsJsonAsync("/api/auth/sign-in", new { name = identifier, password });

    private static async Task<Guid> SignedInIdAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("user").GetProperty("id").GetGuid();

    private static async Task<string> TokenOfAsync(HttpResponseMessage invite) =>
        Uri.UnescapeDataString(JsonDocument.Parse(await invite.Content.ReadAsStringAsync()).RootElement.GetProperty("inviteUrl").GetString()!.Split('/').Last());
}
