using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// Revocation on the WIRE, which is the only place it can be proved.
///
/// <para>A JWT is a signed statement about the past. Before this it kept being believed for its full
/// 24 hours no matter what happened to the account behind it: changing a password left every token
/// minted under the old one working, and an account could not be switched off at all. The claim these
/// tests exist to check is not "the column was written" but "the very next request is refused" — and
/// the refusal happens during token validation, above the mediator, so a test that dispatches a
/// command cannot see it.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host, real Postgres, real middleware pipeline. Every
/// assertion drives a live token through an actual HTTP request.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class SessionRevocationE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public SessionRevocationE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_live_token_works_until_something_ends_it()
    {
        // The control. Without it, every assertion below could pass because the endpoint was
        // unreachable for some unrelated reason.
        var account = await SeedAccountAsync();

        (await GetMeAsync(account.Token)).StatusCode.ShouldBe(HttpStatusCode.OK, "a token for a live account must be believed");
    }

    [Fact]
    public async Task Deactivating_an_account_refuses_its_next_request()
    {
        // The whole point: not "when the token expires", but now. Someone deactivating an account is
        // usually reacting to something, and a day of continued access is the thing they are trying
        // to stop.
        var account = await SeedAccountAsync();

        (await GetMeAsync(account.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await DeactivateAsync(account.UserId);

        (await GetMeAsync(account.Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the same token, one write later");
    }

    [Fact]
    public async Task Reactivating_lets_the_account_back_in()
    {
        // Deactivation is reversible — that is what makes it a different thing from deletion.
        var account = await SeedAccountAsync();

        await DeactivateAsync(account.UserId);
        await ReactivateAsync(account.UserId);

        // The old token stays dead: deactivation rotated the stamp, and reactivation does not un-rotate
        // it. Signing in again is the way back, which is correct — the tokens were revoked on purpose.
        (await GetMeAsync(account.Token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var signIn = await SignInAsync(account.Email, "correct-horse-battery");
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(signIn, "a reactivated account can sign in"));
    }

    [Fact]
    public async Task A_deactivated_account_cannot_sign_in_even_with_the_right_password()
    {
        var account = await SeedAccountAsync();

        await DeactivateAsync(account.UserId);

        var response = await SignInAsync(account.Email, "correct-horse-battery");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(response, "403 — the password was right, the account is off"));
        (await CodeOfAsync(response)).ShouldBe("account_deactivated");
    }

    [Fact]
    public async Task A_wrong_password_on_a_deactivated_account_still_reads_as_wrong_credentials()
    {
        // Otherwise sign-in becomes an oracle: "deactivated" for an address that exists, "invalid" for
        // one that does not, and anyone can enumerate accounts by watching which answer they get.
        var account = await SeedAccountAsync();

        await DeactivateAsync(account.UserId);

        var response = await SignInAsync(account.Email, "not-the-password");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOfAsync(response)).ShouldBe("invalid_credentials");
    }

    [Fact]
    public async Task Changing_a_password_ends_every_other_session_and_keeps_the_one_that_asked()
    {
        // Someone changes their password because they think a session is not theirs. If the other
        // sessions survive, the act is cosmetic — and if their own does not, the product signs them
        // out for doing the right thing.
        var account = await SeedAccountAsync();
        var otherDevice = account.Token;

        var changed = await PostAsync("/api/auth/change-password", account.Token, new { currentPassword = "correct-horse-battery", newPassword = "a-brand-new-passphrase" });
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(changed, "200"));

        var reissued = JsonDocument.Parse(await changed.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        reissued.ShouldNotBeNullOrWhiteSpace("the caller is handed a token under the new stamp, or their own change signs them out");

        (await GetMeAsync(otherDevice)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the token minted before the change is dead");
        (await GetMeAsync(reissued!)).StatusCode.ShouldBe(HttpStatusCode.OK, "the token handed back with the change is live");
    }

    [Fact]
    public async Task A_reset_link_sets_a_new_password_and_kills_the_sessions_that_preceded_it()
    {
        // The full recovery loop, end to end: an admin issues a link, the locked-out person spends it,
        // the old password stops working, the new one works, and whatever sessions existed are gone.
        var account = await SeedAccountAsync();
        var beforeReset = account.Token;

        var link = await IssueResetLinkAsync(account.UserId);
        var token = link[(link.LastIndexOf('/') + 1)..];

        var reset = await _factory.CreateClient().PostAsJsonAsync($"/api/auth/reset-password/{token}", new { newPassword = "recovered-passphrase-x" });
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent, await DescribeAsync(reset, "204"));

        (await GetMeAsync(beforeReset)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a reset that left old sessions alive hands the account back while whoever prompted it still holds one");
        (await SignInAsync(account.Email, "correct-horse-battery")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the old password is gone");
        (await SignInAsync(account.Email, "recovered-passphrase-x")).StatusCode.ShouldBe(HttpStatusCode.OK, "the new one works");
    }

    [Fact]
    public async Task A_reset_link_works_once()
    {
        var account = await SeedAccountAsync();
        var link = await IssueResetLinkAsync(account.UserId);
        var token = link[(link.LastIndexOf('/') + 1)..];

        await _factory.CreateClient().PostAsJsonAsync($"/api/auth/reset-password/{token}", new { newPassword = "first-recovery-pass" });

        var second = await _factory.CreateClient().PostAsJsonAsync($"/api/auth/reset-password/{token}", new { newPassword = "second-recovery-pass" });

        second.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(second, "404 — the token was spent"));
        (await CodeOfAsync(second)).ShouldBe("password_reset_not_usable");
    }

    [Fact]
    public async Task A_reset_link_cannot_revive_a_deactivated_account()
    {
        // Otherwise deactivation is undone by a piece of paper issued before it.
        var account = await SeedAccountAsync();
        var link = await IssueResetLinkAsync(account.UserId);
        var token = link[(link.LastIndexOf('/') + 1)..];

        await DeactivateAsync(account.UserId);

        var reset = await _factory.CreateClient().PostAsJsonAsync($"/api/auth/reset-password/{token}", new { newPassword = "should-not-work-pass" });

        reset.StatusCode.ShouldBe(HttpStatusCode.NotFound, await DescribeAsync(reset, "404"));
    }

    [Fact]
    public async Task An_invented_reset_token_says_exactly_what_a_spent_one_says()
    {
        // Distinguishing them tells a guesser which guesses were once real.
        var account = await SeedAccountAsync();
        var link = await IssueResetLinkAsync(account.UserId);
        var token = link[(link.LastIndexOf('/') + 1)..];

        await _factory.CreateClient().PostAsJsonAsync($"/api/auth/reset-password/{token}", new { newPassword = "first-recovery-pass" });

        var spent = await _factory.CreateClient().PostAsJsonAsync($"/api/auth/reset-password/{token}", new { newPassword = "x-passphrase-here" });
        var invented = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password/not-a-real-token", new { newPassword = "x-passphrase-here" });

        invented.StatusCode.ShouldBe(spent.StatusCode);
        (await CodeOfAsync(invented)).ShouldBe(await CodeOfAsync(spent));
    }

    [Fact]
    public async Task Issuing_a_reset_link_is_not_something_an_ordinary_account_can_do()
    {
        // It hands out a way into someone else's account. Global-admin only, and asserted on the wire
        // because the marker is the only thing standing between an ordinary member and that.
        var account = await SeedAccountAsync();

        var response = await PostAsync($"/api/admin/accounts/{account.UserId}/reset-link", account.Token, new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await DescribeAsync(response, "403"));
    }

    [Fact]
    public async Task A_token_minted_before_the_stamp_existed_is_not_believed()
    {
        // Migration 0114 gave every account a stamp; tokens issued before it carry no claim to compare.
        // Believing those would leave the bypass open for as long as the oldest one lives.
        var account = await SeedAccountAsync();
        var stampless = MintToken(account.UserId, stamp: null);

        (await GetMeAsync(stampless)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetMeAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostAsync(string url, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpResponseMessage> SignInAsync(string email, string password) =>
        await _factory.CreateClient().PostAsJsonAsync("/api/auth/sign-in", new { name = email, password });

    /// <summary>
    /// Deactivation and reset issuance go through the SERVICE rather than the admin endpoint, so a
    /// failure in one of these tests points at revocation rather than at whether the seeded account
    /// happened to hold the Admin role.
    /// </summary>
    private async Task DeactivateAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Core.Services.Users.IAccountLifecycleService>().DeactivateAsync(userId, CancellationToken.None);
    }

    private async Task ReactivateAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Core.Services.Users.IAccountLifecycleService>().ReactivateAsync(userId, CancellationToken.None);
    }

    private async Task<string> IssueResetLinkAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var link = await scope.ServiceProvider.GetRequiredService<Core.Services.Users.IAccountLifecycleService>().IssueResetAsync(userId, CancellationToken.None);

        return link.ResetUrl;
    }

    private async Task<(Guid UserId, string Email, string Token)> SeedAccountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"lifecycle-{suffix}@test.local",
            Name = $"Lifecycle {suffix}",
            PasswordHash = hasher.Hash("correct-horse-battery"),
            SecurityStamp = Guid.NewGuid(),
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
        };

        db.User.Add(user);
        await db.SaveChangesAsync();

        return (user.Id, user.Email, MintToken(user.Id, user.SecurityStamp));
    }

    private static string MintToken(Guid userId, Guid? stamp)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };

        if (stamp != null) claims.Add(new Claim(SessionValidator.SecurityStampClaim, stamp.Value.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TaskLaunchApiFactory.JwtKey));
        var jwt = new JwtSecurityToken(claims: claims, notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;

    private static async Task<string> DescribeAsync(HttpResponseMessage response, string expectation) =>
        $"expected {expectation}; got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}
