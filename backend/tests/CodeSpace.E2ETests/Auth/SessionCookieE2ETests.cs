using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// The browser session on the wire: a sign-in hands back an HttpOnly cookie no page script can read, the cookie alone
/// is a session for the API, a write riding it needs the header another site cannot set, a header-carrying client is
/// untouched by any of it, and every way a session ends — signing out, changing the password, deactivation — ends
/// the cookie's.
///
/// <para>Tier: 🟢 High-fidelity — the real app host, real Postgres, the real middleware pipeline (CORS, the bearer
/// handler and its events, authorization, the controllers). Every assertion reads what a browser would receive or
/// sends what a browser would send. No cookie jar: each request carries exactly the cookie the test names.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class SessionCookieE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string Password = "correct-horse-battery";

    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public SessionCookieE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Theory]
    [InlineData("https", true)]
    [InlineData("http", false)]
    public async Task Signing_in_hands_the_browser_an_http_only_strict_cookie_that_is_secure_exactly_over_https(string scheme, bool secure)
    {
        var account = await AccountAsync();

        var signIn = await _tenants.Client(new Uri($"{scheme}://localhost")).PostAsJsonAsync("/api/auth/sign-in", new { name = account.Email, password = Password });

        signIn.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(signIn));

        var cookie = SessionCookieOf(signIn).ShouldNotBeNull($"a sign-in must hand the browser its session — Set-Cookie: {string.Join(" | ", SetCookies(signIn))}");

        HasAttribute(cookie, "httponly").ShouldBeTrue("no script on the page may read the session");
        HasAttribute(cookie, "samesite=strict").ShouldBeTrue("no request another site starts may carry it");
        HasAttribute(cookie, "path=/api").ShouldBeTrue("only the API needs it");
        HasAttribute(cookie, "secure").ShouldBe(secure, $"over {scheme} — Set-Cookie: {cookie}");
        MaxAgeOf(cookie).ShouldBeInRange(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(24), "the cookie lives exactly as long as the token it carries");

        (await TokenInBodyAsync(signIn)).ShouldBe(CookieValue(cookie), "API clients still read the token from the body; the cookie carries that same session");
    }

    [Fact]
    public async Task The_cookie_alone_is_a_session_and_no_cookie_is_none()
    {
        var account = await AccountAsync();
        var cookie = await SignInAsync(account.Email);

        (await SendAsync(HttpMethod.Get, "/api/users/me", cookie: cookie)).StatusCode.ShouldBe(HttpStatusCode.OK, "a GET needs nothing but the cookie");
        (await SendAsync(HttpMethod.Get, "/api/auth/session", cookie: cookie)).StatusCode.ShouldBe(HttpStatusCode.NoContent, "the probe the SPA asks 'am I signed in' with");
        (await SendAsync(HttpMethod.Get, "/api/auth/session")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "no cookie, no header: nobody");
    }

    [Theory]
    [InlineData("POST", "/api/auth/session")]
    [InlineData("PUT", "/api/teams/cost-cap")]
    [InlineData("PATCH", "/api/teams/members/00000000-0000-0000-0000-000000000001")]
    [InlineData("DELETE", "/api/auth/session")]
    public async Task A_write_riding_the_cookie_must_carry_the_header_another_site_cannot_set(string method, string path)
    {
        var account = await AccountAsync();
        var cookie = await SignInAsync(account.Email);

        var forged = await SendAsync(new HttpMethod(method), path, cookie: cookie);
        var fromTheApp = await SendAsync(new HttpMethod(method), path, cookie: cookie, csrf: true);

        forged.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"{method} {path} with the cookie and no {SessionCookie.CsrfHeader} is what a form on another site sends — {await TenantHarness.DescribeAsync(forged)}");
        SessionCookieOf(forged).ShouldBeNull("a refused write changes nothing, the cookie included");
        fromTheApp.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, $"the control: the same cookie with the header is a session — {await TenantHarness.DescribeAsync(fromTheApp)}");
    }

    [Fact]
    public async Task A_bearer_client_writes_without_the_header_and_a_stray_cookie_never_changes_who_it_is()
    {
        // The CLI, MCP and every API client: an Authorization header, no CSRF header, and — on a developer's machine —
        // possibly a browser's cookie for someone else in the same jar.
        var browserUser = await AccountAsync();
        var apiClient = await AccountAsync();
        var strayCookie = await SignInAsync(browserUser.Email);
        var bearer = await TokenInBodyAsync(await SignInResponseAsync(apiClient.Email));

        var me = await SendAsync(HttpMethod.Get, "/api/users/me", cookie: strayCookie, bearer: bearer);
        var write = await SendAsync(HttpMethod.Post, "/api/auth/change-password", cookie: strayCookie, bearer: bearer, body: new { currentPassword = Password, newPassword = "a-brand-new-passphrase" });

        me.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(me));
        JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("email").GetString().ShouldBe(apiClient.Email, "the header decides who the caller is");
        write.StatusCode.ShouldBe(HttpStatusCode.OK, $"a header-authenticated write needs no CSRF header — {await TenantHarness.DescribeAsync(write)}");
    }

    [Fact]
    public async Task A_dead_header_is_not_rescued_by_a_live_cookie()
    {
        var account = await AccountAsync();
        var cookie = await SignInAsync(account.Email);
        var deadBearer = TestToken.Mint(account.Id, Guid.NewGuid());

        var response = await SendAsync(HttpMethod.Get, "/api/users/me", cookie: cookie, bearer: deadBearer);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a caller that sent a header gets the header's answer");
    }

    [Fact]
    public async Task Signing_out_clears_the_cookie_with_the_attributes_it_was_set_with()
    {
        var account = await AccountAsync();
        var cookie = await SignInAsync(account.Email);

        var signOut = await SendAsync(HttpMethod.Delete, "/api/auth/session", cookie: cookie, csrf: true);

        signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent, await TenantHarness.DescribeAsync(signOut));

        var cleared = SessionCookieOf(signOut).ShouldNotBeNull("the page cannot touch an HttpOnly cookie, so only the server can end it in the browser");

        CookieValue(cleared).ShouldBeEmpty();
        HasAttribute(cleared, "expires=Thu, 01 Jan 1970 00:00:00 GMT").ShouldBeTrue(cleared);
        HasAttribute(cleared, "path=/api").ShouldBeTrue("cleared at the path it was set on, or the browser keeps it");
        HasAttribute(cleared, "samesite=strict").ShouldBeTrue(cleared);
    }

    /// <summary>
    /// A page script rides the cookie and can send the CSRF header, so it can call anything that mints a session. Were
    /// the minted token in the body, the script would read a portable 24-hour bearer out of it — the very theft the
    /// cookie exists to stop. The concrete path: any account opens a workspace and invites the victim's address; the
    /// victim's page, signed in, accepts as the victim, and the response mints the victim a token. So a request the
    /// cookie carried gets its session back only as the cookie; a header-carrying client still reads it from the body.
    /// </summary>
    [Theory]
    [InlineData("sign-in", true)]
    [InlineData("sign-in", false)]
    [InlineData("change-password", true)]
    [InlineData("change-password", false)]
    [InlineData("accept", true)]
    [InlineData("accept", false)]
    public async Task A_session_minted_for_a_request_the_cookie_carried_reaches_the_browser_only_as_the_cookie(string endpoint, bool ridesCookie)
    {
        var account = await AccountAsync();
        var session = await SignInAsync(account.Email);
        var (path, body) = await SessionMintingRequestAsync(endpoint, account.Email);

        var response = ridesCookie ? await SendAsync(HttpMethod.Post, path, cookie: session, csrf: true, body: body) : await SendAsync(HttpMethod.Post, path, bearer: session, body: body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{endpoint} — {await TenantHarness.DescribeAsync(response)}");

        var raw = await response.Content.ReadAsStringAsync();
        var minted = CookieValue(SessionCookieOf(response).ShouldNotBeNull($"{endpoint} still hands the browser its session as the HttpOnly cookie"));

        JsonDocument.Parse(raw).RootElement.TryGetProperty("token", out _).ShouldBe(!ridesCookie, $"{endpoint} riding the cookie={ridesCookie} — body: {raw}");

        if (ridesCookie) raw.ShouldNotContain(minted, Case.Sensitive, "no form of the minted session may reach a body a page script reads");
        else JsonDocument.Parse(raw).RootElement.GetProperty("token").GetString().ShouldBe(minted, "a bearer client reads the session it was minted from the body, exactly as before");
    }

    [Fact]
    public async Task Changing_the_password_through_the_cookie_replaces_it_and_kills_the_one_before()
    {
        var account = await AccountAsync();
        var before = await SignInAsync(account.Email);

        var changed = await SendAsync(HttpMethod.Post, "/api/auth/change-password", cookie: before, csrf: true, body: new { currentPassword = Password, newPassword = "a-brand-new-passphrase" });

        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(changed));

        var after = CookieValue(SessionCookieOf(changed).ShouldNotBeNull("the change rotates the stamp, so the browser needs the session minted under the new one"));

        (await SendAsync(HttpMethod.Get, "/api/users/me", cookie: before)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the cookie minted before the change is dead");
        (await SendAsync(HttpMethod.Get, "/api/users/me", cookie: after)).StatusCode.ShouldBe(HttpStatusCode.OK, "the one handed back with the change is live");
    }

    [Fact]
    public async Task Deactivating_the_account_ends_its_cookie_session_on_the_next_request()
    {
        var account = await AccountAsync();
        var cookie = await SignInAsync(account.Email);

        (await SendAsync(HttpMethod.Get, "/api/users/me", cookie: cookie)).StatusCode.ShouldBe(HttpStatusCode.OK, "the control");

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Core.Services.Users.IAccountLifecycleService>().DeactivateAsync(account.Id, CancellationToken.None);

        (await SendAsync(HttpMethod.Get, "/api/users/me", cookie: cookie)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the same cookie, one write later");
    }

    [Fact]
    public async Task A_pre_cookie_token_is_exchanged_for_a_cookie_carrying_that_same_token()
    {
        // A browser signed in before the cookie existed holds its token in localStorage. It trades it, once, for the
        // cookie. The SAME token: minting a fresh one here would turn the exchange into a way to extend a stolen token.
        var account = await AccountAsync();
        var bearer = await TokenInBodyAsync(await SignInResponseAsync(account.Email));

        var exchange = await SendAsync(HttpMethod.Post, "/api/auth/session", bearer: bearer);

        exchange.StatusCode.ShouldBe(HttpStatusCode.NoContent, await TenantHarness.DescribeAsync(exchange));
        (await exchange.Content.ReadAsStringAsync()).ShouldBeEmpty("the token goes back only as an HttpOnly cookie, never into a body a page could read");

        var cookie = SessionCookieOf(exchange).ShouldNotBeNull("the exchange is what moves the browser onto the cookie");

        CookieValue(cookie).ShouldBe(bearer);
        MaxAgeOf(cookie).ShouldBeLessThanOrEqualTo(TimeSpan.FromHours(24), "the cookie dies with the token it carries, not later");
        (await SendAsync(HttpMethod.Get, "/api/auth/session", cookie: CookieValue(cookie))).StatusCode.ShouldBe(HttpStatusCode.NoContent, "and the browser is now signed in by the cookie alone");
    }

    [Fact]
    public async Task A_dead_token_cannot_be_exchanged_for_a_cookie()
    {
        var account = await AccountAsync();

        var exchange = await SendAsync(HttpMethod.Post, "/api/auth/session", bearer: TestToken.Mint(account.Id, Guid.NewGuid()));

        exchange.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        SessionCookieOf(exchange).ShouldBeNull();
    }

    [Fact]
    public async Task The_dev_spa_origin_may_send_the_header_and_receive_the_cookie_cross_origin()
    {
        // VITE_API_URL=http://localhost:5099 from the Vite dev server on :5180: a credentialed cross-origin call. The
        // preflight must grant the CSRF header, and the response must allow credentials, or the browser drops it.
        const string origin = "http://localhost:5180";
        var account = await AccountAsync();

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/sign-in");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", $"content-type,{SessionCookie.CsrfHeader.ToLowerInvariant()},x-team-id");

        var granted = await _tenants.Client().SendAsync(preflight);

        granted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await TenantHarness.DescribeAsync(granted));
        HeaderOf(granted, "Access-Control-Allow-Origin").ShouldBe(origin);
        HeaderOf(granted, "Access-Control-Allow-Credentials").ShouldBe("true");
        HeaderOf(granted, "Access-Control-Allow-Headers")!.ToLowerInvariant().ShouldContain(SessionCookie.CsrfHeader.ToLowerInvariant());

        var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-in") { Content = JsonContent.Create(new { name = account.Email, password = Password }) };
        signIn.Headers.Add("Origin", origin);
        signIn.Headers.Add(SessionCookie.CsrfHeader, "1");

        var answered = await _tenants.Client().SendAsync(signIn);

        answered.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(answered));
        HeaderOf(answered, "Access-Control-Allow-Origin").ShouldBe(origin);
        HeaderOf(answered, "Access-Control-Allow-Credentials").ShouldBe("true", "without it the browser refuses the credentialed response — and the cookie with it");
        SessionCookieOf(answered).ShouldNotBeNull();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<(Guid Id, string Email)> AccountAsync()
    {
        var email = $"cookie-{Guid.NewGuid():N}@session.test";

        return (await _tenants.AccountAsync(email: email, password: Password), email);
    }

    private async Task<HttpResponseMessage> SignInResponseAsync(string email)
    {
        var response = await _tenants.Client().PostAsJsonAsync("/api/auth/sign-in", new { name = email, password = Password });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return response;
    }

    /// <summary>
    /// The request that mints a session at <paramref name="endpoint"/> for the account at <paramref name="email"/>. The
    /// invitation goes through the product: another account opens a workspace and invites that address, which any
    /// account may do — the step a script's author takes before planting the script.
    /// </summary>
    private async Task<(string Path, object Body)> SessionMintingRequestAsync(string endpoint, string email) => endpoint switch
    {
        "sign-in" => ("/api/auth/sign-in", new { name = email, password = Password }),
        "change-password" => ("/api/auth/change-password", new { currentPassword = Password, newPassword = "a-brand-new-passphrase" }),
        "accept" => ($"/api/invitations/{await InvitationFromAnotherAccountAsync(email)}/accept", new { }),
        _ => throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, null)
    };

    private async Task<string> InvitationFromAnotherAccountAsync(string email)
    {
        var inviter = await _tenants.AccountAsync();

        await GrantEveryAccountsPermissionsAsync(inviter);

        var workspace = await _tenants.SendAsync(HttpMethod.Post, "/api/teams", inviter, null, new { name = $"Workspace {Guid.NewGuid():N}"[..20] });
        workspace.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(workspace));
        var teamId = JsonDocument.Parse(await workspace.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var invitation = await _tenants.SendAsync(HttpMethod.Post, "/api/teams/invitations", inviter, teamId, new { email, role = "Member" });
        invitation.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(invitation));
        var inviteUrl = JsonDocument.Parse(await invitation.Content.ReadAsStringAsync()).RootElement.GetProperty("inviteUrl").GetString()!;

        return inviteUrl[(inviteUrl.LastIndexOf('/') + 1)..];
    }

    /// <summary>What every account holds from the moment it exists — the right to open a workspace — which an account seeded straight to the row does not get by itself.</summary>
    private async Task GrantEveryAccountsPermissionsAsync(Guid userId) => await _tenants.DbAsync(async db =>
    {
        foreach (var permissionId in await db.Permission.Where(p => Permissions.GrantedToEveryAccount.Contains(p.Name)).Select(p => p.Id).ToListAsync())
            db.UserPermission.Add(new UserPermission { Id = Guid.NewGuid(), UserId = userId, PermissionId = permissionId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        return await db.SaveChangesAsync();
    });

    /// <summary>Signs in as a browser does and returns the session cookie's value.</summary>
    private async Task<string> SignInAsync(string email) => CookieValue(SessionCookieOf(await SignInResponseAsync(email)).ShouldNotBeNull());

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? cookie = null, string? bearer = null, bool csrf = false, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (body != null) request.Content = JsonContent.Create(body);
        if (cookie != null) request.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie}");
        if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (csrf) request.Headers.Add(SessionCookie.CsrfHeader, "1");

        return await _tenants.Client().SendAsync(request);
    }

    private static async Task<string> TokenInBodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : Array.Empty<string>();

    private static string? SessionCookieOf(HttpResponseMessage response) =>
        SetCookies(response).SingleOrDefault(c => c.StartsWith($"{SessionCookie.Name}=", StringComparison.Ordinal));

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0][(SessionCookie.Name.Length + 1)..];

    private static TimeSpan MaxAgeOf(string setCookie) =>
        TimeSpan.FromSeconds(long.Parse(setCookie.Split(';').Select(a => a.Trim()).Single(a => a.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))["max-age=".Length..]));

    private static string? HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    /// <summary>Whether a Set-Cookie header carries this attribute — compared whole, so "secure" never matches inside another one.</summary>
    private static bool HasAttribute(string setCookie, string attribute) =>
        setCookie.Split(';').Skip(1).Any(a => string.Equals(a.Trim(), attribute, StringComparison.OrdinalIgnoreCase));
}
