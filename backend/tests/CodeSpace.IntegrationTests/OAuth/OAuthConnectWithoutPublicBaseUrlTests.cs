using Autofac;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Core.Settings.OAuth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Invitations;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using MediatR;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// 🟢 Integration (real container, real handlers, Postgres): a host outside Development that never set
/// <c>App:PublicBaseUrl</c> — the shipped docker-compose API is one. #2104 made the OAuth service inject
/// <c>PublicBaseUrlSetting</c>, whose constructor throws on such a host, so every connect answered 500 whatever return
/// address it asked for, and so did every callback. OAuth now reads the non-throwing return-origin set; the invitation
/// link, which must open somewhere real for someone else, still refuses exactly as before.
///
/// <para>The settings are built from a Production configuration the way the container builds them — the OAuth set
/// through its own constructor, <c>PublicBaseUrlSetting</c> as a registration that throws on resolve — so a service that
/// went back to the throwing setting fails here.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OAuthConnectWithoutPublicBaseUrlTests
{
    private const string CorsOrigin = "https://spa.example";

    private static readonly IConfiguration Production = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Production",
        ["Cors:AllowedOrigins"] = CorsOrigin,
        ["OAuth:CallbackUrl"] = "https://api.example/api/credentials/oauth/callback",
    }).Build();

    private readonly PostgresFixture _fixture;

    public OAuthConnectWithoutPublicBaseUrlTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void The_host_under_test_cannot_mint_a_link()
    {
        using var scope = ProductionScope(caller: null, stub: null);

        Should.Throw<Exception>(() => scope.Resolve<PublicBaseUrlSetting>()).GetBaseException().Message.ShouldContain(PublicBaseUrlSetting.ConfigurationKey);
    }

    [Theory]
    [InlineData("/oauth-callback", "/oauth-callback")]
    [InlineData(null, "/")]
    public async Task A_connect_returning_to_a_path_starts_and_finishes(string? returnUrl, string landing)
    {
        var connector = await SeedConnectorAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, new OAuthTokenResponse { AccessToken = "at" });

        var init = await InitAsync(connector, stub, returnUrl).ConfigureAwait(false);

        using var callback = ProductionScope(caller: null, stub);
        var completed = await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code", BrowserNonce = init.BrowserNonce }).ConfigureAwait(false);

        completed.CredentialId.ShouldNotBeNull();
        completed.ReturnUrl.ShouldBe(landing);
    }

    [Fact]
    public async Task A_connect_returning_to_a_cors_origin_starts_and_hands_off_there()
    {
        // Direct mode: the SPA calls the API from an origin on the CORS list, so it may name that origin to come back to.
        var connector = await SeedConnectorAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, new OAuthTokenResponse { AccessToken = "at" });

        var init = await InitAsync(connector, stub, $"{CorsOrigin}/oauth-callback").ConfigureAwait(false);

        using var callback = ProductionScope(caller: null, stub);
        var handedOff = await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = init.State, Code = "code" }).ConfigureAwait(false);

        handedOff.ReturnUrl.ShouldBe($"{CorsOrigin}/oauth-callback");
    }

    [Fact]
    public async Task A_connect_returning_anywhere_else_is_refused_by_naming_both_settings()
    {
        var connector = await SeedConnectorAsync().ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, new OAuthTokenResponse { AccessToken = "at" });

        var refused = await Should.ThrowAsync<ArgumentException>(async () => await InitAsync(connector, stub, "https://elsewhere.example/oauth-callback").ConfigureAwait(false)).ConfigureAwait(false);

        refused.Message.ShouldContain("App:PublicBaseUrl");
        refused.Message.ShouldContain("Cors:AllowedOrigins");
        FailureClassifier.Classify(refused).Kind.ShouldBe(FailureKind.Invalid, "a 400 the operator can act on, not a 500");
    }

    [Fact]
    public async Task A_callback_for_an_unknown_state_is_a_refusal_not_a_failure()
    {
        using var callback = ProductionScope(caller: null, new StubOAuthClient(ProviderKind.GitLab, new OAuthTokenResponse { AccessToken = "at" }));

        var refused = await Should.ThrowAsync<OAuthCallbackException>(async () => await callback.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { State = "no-such-state", Code = "code" }).ConfigureAwait(false)).ConfigureAwait(false);

        FailureClassifier.Classify(refused).Kind.ShouldBe(FailureKind.Invalid);
    }

    [Fact]
    public async Task An_invitation_still_refuses_to_mint_a_link_nobody_could_open()
    {
        var connector = await SeedConnectorAsync().ConfigureAwait(false);
        using var scope = ProductionScope(connector, stub: null);

        var refused = await Should.ThrowAsync<Exception>(async () => await scope.Resolve<IMediator>().Send(new CreateTeamInvitationCommand { Email = $"invitee-{Guid.NewGuid():N}@x", Role = TeamRole.Member }).ConfigureAwait(false)).ConfigureAwait(false);

        refused.GetBaseException().ShouldBeOfType<InvalidOperationException>().Message.ShouldContain(PublicBaseUrlSetting.ConfigurationKey);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────

    private sealed record Connector(Guid UserId, Guid TeamId, Guid InstanceId);

    private ILifetimeScope ProductionScope(Connector? caller, StubOAuthClient? stub) => _fixture.BeginScope(b =>
    {
        b.Register(_ => new OAuthReturnOriginsSetting(Production)).AsSelf().SingleInstance();
        b.Register(_ => new OAuthCallbackUrlSetting(Production)).AsSelf().SingleInstance();
        b.Register(_ => new PublicBaseUrlSetting(Production)).AsSelf();

        if (stub != null) b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();

        if (caller == null) return;

        b.RegisterInstance(new TestCurrentUser(caller.UserId, "tester")).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
        b.RegisterInstance(new TestCurrentTeam(caller.TeamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
    });

    private async Task<InitCredentialOAuthResult> InitAsync(Connector connector, StubOAuthClient stub, string? returnUrl)
    {
        using var scope = ProductionScope(connector, stub);

        return await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = connector.InstanceId, DisplayName = "connect", ReturnUrl = returnUrl }).ConfigureAwait(false);
    }

    private async Task<Connector> SeedConnectorAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = $"owner-{suffix}" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitLab, DisplayName = "OAuth-capable", BaseUrl = $"https://gitlab-{suffix}.local", OauthClientId = "test-client", OauthClientSecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt("test-secret") };

        db.User.Add(user);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Connector(user.Id, team.Id, instance.Id);
    }

    private sealed class SingleProviderRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;
        public SingleProviderRegistry(IOAuthClient client) { _client = client; }
        public IOAuthClient Get(ProviderKind kind) => _client;
    }
}
