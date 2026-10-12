using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline with ASP.NET's own forwarded-headers handling switched on, Postgres): behind a
/// TLS-terminating proxy the app sees plain HTTP, and the browser sees HTTPS. The binding cookie must follow what the
/// browser sees, which the host learns only from the forwarded-headers configuration it is deployed with.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class OAuthConnectBehindTlsProxyE2ETests : IClassFixture<ForwardedHeadersGitWireApiFactory>
{
    private const string BindingCookie = "codespace_oauth_nonce";

    private readonly ForwardedHeadersGitWireApiFactory _factory;
    private readonly TenantHarness _tenants;

    public OAuthConnectBehindTlsProxyE2ETests(ForwardedHeadersGitWireApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Theory]
    [InlineData("https", true)]
    [InlineData(null, false)]
    public async Task The_binding_cookie_is_secure_when_the_proxy_says_the_browser_is_on_https(string? forwardedProto, bool secure)
    {
        var (userId, teamId, instanceId) = await SeedConnectorAsync();
        var request = _tenants.Request(HttpMethod.Post, "/api/credentials/oauth/init", userId, teamId, new { providerInstanceId = instanceId, displayName = "connect", returnUrl = (string?)null });

        if (forwardedProto != null) request.Headers.Add("X-Forwarded-Proto", forwardedProto);

        var init = await _tenants.Client().SendAsync(request);

        var cookie = (init.Headers.TryGetValues("Set-Cookie", out var values) ? values : Array.Empty<string>()).SingleOrDefault(c => c.StartsWith($"{BindingCookie}=", StringComparison.Ordinal));

        OAuthConnectBindingE2ETests.HasAttribute(cookie.ShouldNotBeNull(await TenantHarness.DescribeAsync(init)), "secure").ShouldBe(secure, $"X-Forwarded-Proto: {forwardedProto ?? "(none)"} — Set-Cookie: {cookie}");
    }

    private async Task<(Guid UserId, Guid TeamId, Guid InstanceId)> SeedConnectorAsync()
    {
        var admin = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((admin, TeamRole.Admin));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.Git, DisplayName = "OAuth app", BaseUrl = $"https://git.stub.test/{Guid.NewGuid():N}", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return (admin, teamId, instance.Id);
    }
}
