using System.Net;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline in the Production environment, Postgres; only the provider's token endpoint is a
/// stub): a host outside Development that never set <c>App:PublicBaseUrl</c>. #2104 left every OAuth start and every
/// callback on such a host answering 500, because the OAuth service injected the setting that throws when the key is
/// unset. A connect needs no origin to return to a path, and may name any origin the deployment serves its SPA from;
/// only the invitation link, which someone else must be able to open, still refuses to be minted.
///
/// <para>Diagnose a red here by starting the API with <c>ASPNETCORE_ENVIRONMENT=Production</c> and no
/// <c>App__PublicBaseUrl</c>, then POST <c>/api/credentials/oauth/init</c>: a 500 with "App:PublicBaseUrl is required"
/// in the log means something in the OAuth path resolves <c>PublicBaseUrlSetting</c> again.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class OAuthConnectWithoutPublicBaseUrlE2ETests : IClassFixture<ProductionWithoutPublicBaseUrlGitWireApiFactory>
{
    private const string BindingCookie = "codespace_oauth_nonce";

    private readonly ProductionWithoutPublicBaseUrlGitWireApiFactory _factory;
    private readonly TenantHarness _tenants;

    public OAuthConnectWithoutPublicBaseUrlE2ETests(ProductionWithoutPublicBaseUrlGitWireApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Theory]
    [InlineData("/oauth-callback", "/oauth-callback?oauthCredentialId=")]
    [InlineData(null, "/?oauthCredentialId=")]
    public async Task A_connect_returning_to_a_path_starts_and_completes(string? returnUrl, string landing)
    {
        var (userId, teamId, instanceId) = await SeedConnectorAsync();

        var init = await InitAsync(userId, teamId, instanceId, returnUrl);

        init.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(init));
        var callback = await CallbackAsync(await StateOfAsync(init), $"code-{Guid.NewGuid():N}", BindingCookieValueOf(init));

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback));
        callback.Headers.Location!.OriginalString.ShouldStartWith(landing);
    }

    [Fact]
    public async Task A_connect_returning_to_a_cors_origin_starts()
    {
        var (userId, teamId, instanceId) = await SeedConnectorAsync();

        var init = await InitAsync(userId, teamId, instanceId, $"{ProductionWithoutPublicBaseUrlGitWireApiFactory.CorsOrigin}/oauth-callback");

        init.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(init));
    }

    [Fact]
    public async Task A_connect_returning_anywhere_else_is_a_400_naming_both_settings()
    {
        var (userId, teamId, instanceId) = await SeedConnectorAsync();

        var init = await InitAsync(userId, teamId, instanceId, "https://elsewhere.example/oauth-callback");

        init.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(init));
        var body = JsonDocument.Parse(await init.Content.ReadAsStringAsync()).RootElement;
        var message = body.GetProperty("message").GetString();
        message.ShouldNotBeNull().ShouldContain("App:PublicBaseUrl");
        message.ShouldContain("Cors:AllowedOrigins");
        body.GetProperty("code").GetString().ShouldBe("invalid_request", "the SPA asks again with a path on exactly this answer (frontend/src/hooks/use-oauth-flow.ts), so a page on an origin the API does not list can still connect");
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.ProviderInstanceId == instanceId))).ShouldBe(0, "asking again must not leave the refused start behind");
    }

    [Fact]
    public async Task A_callback_for_an_unknown_state_is_a_400_not_a_500()
    {
        var callback = await CallbackAsync("no-such-state", "code", bindingCookie: null);

        callback.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(callback));
    }

    [Fact]
    public async Task An_invitation_still_fails_fast_without_the_key()
    {
        // Unchanged on purpose: an invite is a link handed to someone else, and the built-in default is the dev SPA.
        var (userId, teamId, _) = await SeedConnectorAsync();

        var invite = await _tenants.SendAsync(HttpMethod.Post, "/api/teams/invitations", userId, teamId, new { email = $"invitee-{Guid.NewGuid():N}@tenant.test", role = "Member" });

        invite.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, await TenantHarness.DescribeAsync(invite));
    }

    private async Task<(Guid UserId, Guid TeamId, Guid InstanceId)> SeedConnectorAsync()
    {
        var admin = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((admin, TeamRole.Owner));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "OAuth app", BaseUrl = $"https://git.stub.test/{Guid.NewGuid():N}", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return (admin, teamId, instance.Id);
    }

    private async Task<HttpResponseMessage> InitAsync(Guid userId, Guid teamId, Guid instanceId, string? returnUrl) =>
        await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/oauth/init", userId, teamId, new { providerInstanceId = instanceId, displayName = "connect", returnUrl });

    private async Task<HttpResponseMessage> CallbackAsync(string state, string code, string? bindingCookie)
    {
        var request = _tenants.Request(HttpMethod.Get, $"/api/credentials/oauth/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", userId: null, teamId: null);

        if (bindingCookie != null) request.Headers.Add("Cookie", $"{BindingCookie}={bindingCookie}");

        return await _tenants.Client().SendAsync(request);
    }

    private static async Task<string> StateOfAsync(HttpResponseMessage init) =>
        JsonDocument.Parse(await init.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString()!;

    private static string BindingCookieValueOf(HttpResponseMessage init) =>
        init.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith($"{BindingCookie}=", StringComparison.Ordinal)).Split(';')[0][(BindingCookie.Length + 1)..];
}
