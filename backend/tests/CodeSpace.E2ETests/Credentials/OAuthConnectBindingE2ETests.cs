using System.Net;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Settings.Application;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline, cookies on the wire, Postgres; only the provider's token endpoint is a stub):
/// an OAuth connect completes only in the browser that started it, and only ever lands back on this site.
///
/// <para>The capture this pins: the callback is anonymous by necessity, and the state token was its only proof. An
/// Admin of team B started a connect on their own OAuth app and sent the authorize link to a victim; the victim's
/// browser finished it, and team B received a credential holding the VICTIM's provider token, plus an act-as identity
/// naming the victim's provider account against the attacker. The return address was also passed through verbatim,
/// so the victim's browser then left for the attacker's page.</para>
///
/// <para>Now the start of a connect sets an HttpOnly cookie in the starting browser and stores only its hash on the
/// pending state; the callback must present it before anything is consumed, exchanged or persisted.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class OAuthConnectBindingE2ETests : IClassFixture<GitWireApiFactory>
{
    private const string BindingCookie = "codespace_oauth_nonce";
    private const string CallbackPath = "/api/credentials/oauth/callback";

    private readonly GitWireApiFactory _factory;
    private readonly TenantHarness _tenants;

    public OAuthConnectBindingE2ETests(GitWireApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    /// <summary>
    /// Secure follows the scheme the request actually arrived on. A browser drops a Secure cookie set over plain HTTP —
    /// Safari on http://localhost too — so an always-Secure binding left every plain-HTTP deployment unable to finish a
    /// connect: the callback arrived without the nonce and was refused. Each scheme is driven through to a credential.
    /// </summary>
    [Theory]
    [InlineData("http", false)]
    [InlineData("https", true)]
    public async Task The_binding_cookie_is_http_only_lax_and_secure_exactly_over_https_and_the_connect_completes(string scheme, bool secure)
    {
        var initiator = await SeedConnectorAsync();
        var origin = new Uri($"{scheme}://localhost");

        var init = await InitAsync(initiator, returnUrl: null, origin);

        var cookie = BindingCookieOf(init).ShouldNotBeNull($"the start of a connect must bind the browser — Set-Cookie: {string.Join(" | ", SetCookies(init))}");
        HasAttribute(cookie, "httponly").ShouldBeTrue("script on the page must never read the binding");
        HasAttribute(cookie, "samesite=lax").ShouldBeTrue("Lax is what lets the provider's top-level redirect carry it back");
        HasAttribute(cookie, $"path={CallbackPath}").ShouldBeTrue("only the callback ever needs it");
        HasAttribute(cookie, "secure").ShouldBe(secure, $"over {scheme} — Set-Cookie: {cookie}");
        (await init.Content.ReadAsStringAsync()).ShouldNotContain(CookieValue(cookie), Case.Sensitive, "the binding travels only as an HttpOnly cookie, never in a body a page could read");

        var code = $"{scheme}-code-{Guid.NewGuid():N}";
        var callback = await CallbackAsync(await StateOfAsync(init), code, CookieValue(cookie), caller: null, origin);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback));
        var deleted = SetCookies(callback).Single(c => c.StartsWith($"{BindingCookie}=;", StringComparison.Ordinal));
        HasAttribute(deleted, "secure").ShouldBe(secure, "the spent binding is cleared with the attributes it was set with");
        HasAttribute(deleted, $"path={CallbackPath}").ShouldBeTrue("cleared at the path it was set on, or the browser keeps it");
        (await _tenants.DbAsync(db => db.Credential.AsNoTracking().CountAsync(c => c.ProviderInstanceId == initiator.InstanceId))).ShouldBe(1, $"the connect started over {scheme} must land a credential");
    }

    [Fact]
    public async Task A_forwarded_proto_header_is_not_trusted_by_a_host_not_configured_for_a_proxy()
    {
        // Only the host's forwarded-headers configuration may change the effective scheme; a header any client can send
        // must not make the binding Secure on a plain-HTTP deployment, where the browser would then drop it.
        var initiator = await SeedConnectorAsync();
        var request = _tenants.Request(HttpMethod.Post, "/api/credentials/oauth/init", initiator.UserId, initiator.TeamId, new { providerInstanceId = initiator.InstanceId, displayName = "connect", returnUrl = (string?)null });
        request.Headers.Add("X-Forwarded-Proto", "https");

        var init = await _tenants.Client().SendAsync(request);

        HasAttribute(BindingCookieOf(init).ShouldNotBeNull(), "secure").ShouldBeFalse();
    }

    [Fact]
    public async Task A_callback_from_a_browser_that_did_not_start_the_connect_completes_nothing()
    {
        var attacker = await SeedConnectorAsync();
        var victim = await SeedConnectorAsync();
        var state = await StateOfAsync(await InitAsync(attacker, returnUrl: null));

        // The victim's browser: the victim's own session and team, and no binding for this flow.
        var callback = await CallbackAsync(state, code: $"victim-code-{Guid.NewGuid():N}", bindingCookie: null, victim);

        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(callback));
        callback.Headers.Location.ShouldBeNull("a refused callback redirects nowhere");
        await ShouldHaveCompletedNothingAsync(attacker, state);
    }

    [Fact]
    public async Task A_callback_carrying_the_binding_of_a_different_connect_completes_nothing()
    {
        var attacker = await SeedConnectorAsync();
        var victim = await SeedConnectorAsync();
        var attackerState = await StateOfAsync(await InitAsync(attacker, returnUrl: null));
        var victimCookie = CookieValue(BindingCookieOf(await InitAsync(victim, returnUrl: null))!);

        var callback = await CallbackAsync(attackerState, code: $"victim-code-{Guid.NewGuid():N}", victimCookie, victim);

        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(callback));
        await ShouldHaveCompletedNothingAsync(attacker, attackerState);
    }

    [Fact]
    public async Task The_starting_browser_completes_the_connect_and_returns_to_the_public_origin()
    {
        var initiator = await SeedConnectorAsync();
        var returnUrl = $"{PublicBaseUrl()}/oauth-callback";
        var init = await InitAsync(initiator, returnUrl);
        var code = $"own-code-{Guid.NewGuid():N}";

        var callback = await CallbackAsync(await StateOfAsync(init), code, CookieValue(BindingCookieOf(init)!), caller: null);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback));
        callback.Headers.Location!.OriginalString.ShouldStartWith($"{returnUrl}?oauthCredentialId=");
        SetCookies(callback).ShouldContain(c => c.StartsWith($"{BindingCookie}=;", StringComparison.Ordinal), "a spent binding is cleared from the browser");

        using var scope = _factory.Services.CreateScope();
        var credential = await _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.ProviderInstanceId == initiator.InstanceId));

        credential.OwnerUserId.ShouldBe(initiator.UserId);
        scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Decrypt(credential.EncryptedPayload).ShouldContain($"token-for-{code}");
    }

    [Theory]
    [InlineData("https://attacker.example/landing")]
    [InlineData("//attacker.example/x")]
    [InlineData("/\\attacker.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://localhost:5180.attacker.example/x")]
    [InlineData("http://attacker@localhost:5180/x")]
    public async Task A_return_address_off_this_site_is_refused_before_a_connect_starts(string returnUrl)
    {
        var initiator = await SeedConnectorAsync();

        var init = await InitAsync(initiator, returnUrl);

        init.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(init));
        (await PendingCountAsync(initiator.InstanceId)).ShouldBe(0, "a refused start leaves no pending connect behind");
    }

    [Fact]
    public async Task A_return_path_on_this_site_is_kept()
    {
        var initiator = await SeedConnectorAsync();
        var init = await InitAsync(initiator, "/settings/credentials");

        var callback = await CallbackAsync(await StateOfAsync(init), $"code-{Guid.NewGuid():N}", CookieValue(BindingCookieOf(init)!), caller: null);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback));
        callback.Headers.Location!.OriginalString.ShouldStartWith("/settings/credentials?oauthCredentialId=");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record Connector(Guid UserId, Guid TeamId, Guid InstanceId);

    private async Task<Connector> SeedConnectorAsync()
    {
        var admin = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((admin, TeamRole.Admin));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "Shared OAuth app", BaseUrl = $"https://git.stub.test/{Guid.NewGuid():N}", OauthClientId = "shared-client", OauthClientSecretEnc = encryptor.Encrypt("shared-secret"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return new Connector(admin, teamId, instance.Id);
    }

    private async Task<HttpResponseMessage> InitAsync(Connector connector, string? returnUrl, Uri? origin = null) =>
        await _tenants.Client(origin).SendAsync(_tenants.Request(HttpMethod.Post, "/api/credentials/oauth/init", connector.UserId, connector.TeamId, new { providerInstanceId = connector.InstanceId, displayName = "connect", returnUrl }));

    private async Task<HttpResponseMessage> CallbackAsync(string state, string code, string? bindingCookie, Connector? caller, Uri? origin = null)
    {
        var request = _tenants.Request(HttpMethod.Get, $"{CallbackPath}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", caller?.UserId, caller?.TeamId);

        if (bindingCookie != null) request.Headers.Add("Cookie", $"{BindingCookie}={bindingCookie}");

        return await _tenants.Client(origin).SendAsync(request);
    }

    private async Task ShouldHaveCompletedNothingAsync(Connector attacker, string state)
    {
        _factory.Wire.ExchangedCodes.ShouldNotContain(c => c.StartsWith("victim-code-"), "the victim's code must never reach the token endpoint");

        var (credentials, identities, pending) = await _tenants.DbAsync(async db => (
            await db.Credential.AsNoTracking().CountAsync(c => c.ProviderInstanceId == attacker.InstanceId),
            await db.UserProviderIdentity.AsNoTracking().CountAsync(i => i.ProviderInstanceId == attacker.InstanceId),
            await db.OAuthPendingState.AsNoTracking().CountAsync(s => s.State == state)));

        credentials.ShouldBe(0, "no credential may land in the attacker's team");
        identities.ShouldBe(0, "no act-as identity may be linked from someone else's browser");
        pending.ShouldBe(1, "a refused callback consumes nothing — the state is still the starting browser's to finish");
    }

    private async Task<int> PendingCountAsync(Guid instanceId) =>
        await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.ProviderInstanceId == instanceId));

    private string PublicBaseUrl()
    {
        using var scope = _factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<PublicBaseUrlSetting>().Value;
    }

    private static async Task<string> StateOfAsync(HttpResponseMessage init) =>
        JsonDocument.Parse(await init.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString()!;

    private static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : Array.Empty<string>();

    private static string? BindingCookieOf(HttpResponseMessage response) =>
        SetCookies(response).FirstOrDefault(c => c.StartsWith($"{BindingCookie}=", StringComparison.Ordinal));

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0][(BindingCookie.Length + 1)..];

    /// <summary>Whether a Set-Cookie header carries this attribute — compared whole, so "secure" never matches inside another one.</summary>
    internal static bool HasAttribute(string setCookie, string attribute) =>
        setCookie.Split(';').Skip(1).Any(a => string.Equals(a.Trim(), attribute, StringComparison.OrdinalIgnoreCase));
}
