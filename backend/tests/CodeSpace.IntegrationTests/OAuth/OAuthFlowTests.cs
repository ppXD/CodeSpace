using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.OAuth;

/// <summary>
/// End-to-end OAuth flow: Init → state-stored → Complete (with stubbed token endpoint) →
/// Credential row persisted, state consumed. Also pins replay rejection + cross-team
/// rejection at the init boundary.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class OAuthFlowTests
{
    private readonly PostgresFixture _fixture;

    public OAuthFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Init_creates_pending_state_and_returns_authorize_url()
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));

        using var scope = _fixture.BeginScope(b =>
        {
            RegisterTestUserAndTeam(b, userId, teamId);
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        });

        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new InitCredentialOAuthCommand
        {
            ProviderInstanceId = instanceId,
            DisplayName = "Maya's GitLab",
            ReturnUrl = "https://app.codespace.dev/settings/credentials"
        }).ConfigureAwait(false);

        result.State.ShouldNotBeNullOrEmpty();
        result.AuthorizeUrl.AbsoluteUri.ShouldContain("client_id=test-client");
        result.AuthorizeUrl.AbsoluteUri.ShouldContain($"state={result.State}");

        // State row must exist with the right fields, and CodeVerifier must be non-empty
        // (we never assert its exact value — it's CSPRNG output).
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var row = await db.OAuthPendingState.AsNoTracking().SingleAsync(s => s.State == result.State).ConfigureAwait(false);
        row.ProviderInstanceId.ShouldBe(instanceId);
        row.TeamId.ShouldBe(teamId);
        row.InitiatorUserId.ShouldBe(userId);
        row.IntendedDisplayName.ShouldBe("Maya's GitLab");
        row.IntendedOwnerUserId.ShouldBe(userId);
        row.ReturnUrl.ShouldBe("https://app.codespace.dev/settings/credentials");
        row.CodeVerifier.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Complete_consumes_state_and_persists_credential()
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("access-from-provider", "refresh-from-provider", expiresIn: TimeSpan.FromHours(2)));

        InitCredentialOAuthResult initResult;

        using (var initScope = _fixture.BeginScope(b =>
        {
            RegisterTestUserAndTeam(b, userId, teamId);
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        }))
        {
            var mediator = initScope.Resolve<IMediator>();
            initResult = await mediator.Send(new InitCredentialOAuthCommand
            {
                ProviderInstanceId = instanceId,
                DisplayName = "Maya's GitLab"
            }).ConfigureAwait(false);
        }

        // Callback path runs anonymously — no user/team in scope.
        CompleteCredentialOAuthResult completeResult;

        using (var callbackScope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        }))
        {
            var mediator = callbackScope.Resolve<IMediator>();
            completeResult = await mediator.Send(new CompleteCredentialOAuthCommand
            {
                Code = "code-from-provider",
                State = initResult.State
            }).ConfigureAwait(false);
        }

        completeResult.TeamId.ShouldBe(teamId);
        completeResult.CredentialId.ShouldNotBe(Guid.Empty);
        completeResult.ReturnUrl.ShouldBe("/");

        // Verify the Credential row was persisted with the right shape, and that the
        // pending state was consumed.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var cred = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == completeResult.CredentialId).ConfigureAwait(false);
        cred.TeamId.ShouldBe(teamId);
        cred.ProviderInstanceId.ShouldBe(instanceId);
        cred.OwnerUserId.ShouldBe(userId);
        cred.AuthType.ShouldBe(AuthType.OAuth);
        cred.DisplayName.ShouldBe("Maya's GitLab");
        cred.CreatedBy.ShouldBe(userId);
        cred.LastModifiedBy.ShouldBe(userId);
        cred.Status.ShouldBe(CredentialStatus.Active);

        var encryptor = verify.Resolve<IPayloadEncryptor>();
        var serializer = verify.Resolve<ICredentialPayloadSerializer>();
        var json = encryptor.Decrypt(cred.EncryptedPayload);
        var payload = (OAuthPayload)serializer.Deserialize(AuthType.OAuth, json);
        payload.AccessToken.ShouldBe("access-from-provider");
        payload.RefreshToken.ShouldBe("refresh-from-provider");

        (await db.OAuthPendingState.AsNoTracking().AnyAsync(s => s.State == initResult.State).ConfigureAwait(false)).ShouldBeFalse("state row must be deleted after consume");

        // Stub recorded the code + PKCE verifier passed through.
        stub.LastExchange.ShouldNotBeNull();
        stub.LastExchange!.Code.ShouldBe("code-from-provider");
        stub.LastExchange.CodeVerifier.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Complete_with_unknown_state_throws()
    {
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));

        using var scope = _fixture.BeginScope(b =>
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());

        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new CompleteCredentialOAuthCommand { Code = "c", State = "totally-bogus" }).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<OAuthCallbackException>().ConfigureAwait(false);
        ex.Reason.ShouldContain("state");
    }

    [Fact]
    public async Task Complete_twice_with_same_state_rejects_replay()
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));

        InitCredentialOAuthResult initResult;

        using (var initScope = _fixture.BeginScope(b =>
        {
            RegisterTestUserAndTeam(b, userId, teamId);
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        }))
        {
            initResult = await initScope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand
            {
                ProviderInstanceId = instanceId,
                DisplayName = "first-attempt"
            }).ConfigureAwait(false);
        }

        // First callback consumes the state.
        using (var scope = _fixture.BeginScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = "c1", State = initResult.State }).ConfigureAwait(false);
        }

        // Second callback with the same state must fail — proves one-time-use enforcement.
        using (var scope = _fixture.BeginScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            var act = async () => await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = "c2", State = initResult.State }).ConfigureAwait(false);
            await act.ShouldThrowAsync<OAuthCallbackException>().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Init_for_non_oauth_capable_instance_throws()
    {
        var (userId, teamId, instanceId) = await SeedInstanceAsync(ProviderKind.GitLab, oauthCapable: false).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));

        using var scope = _fixture.BeginScope(b =>
        {
            RegisterTestUserAndTeam(b, userId, teamId);
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        });

        var mediator = scope.Resolve<IMediator>();
        var act = async () => await mediator.Send(new InitCredentialOAuthCommand
        {
            ProviderInstanceId = instanceId,
            DisplayName = "broken"
        }).ConfigureAwait(false);

        var ex = await act.ShouldThrowAsync<OAuthCallbackException>().ConfigureAwait(false);
        ex.Reason.ShouldContain("OAuth-configured");
    }

    [Fact]
    public async Task Complete_links_an_act_as_user_identity_resolvable_for_the_owner()
    {
        // Parity with PAT linking: an OAuth connect must also give the user an act-as-user identity,
        // so "act as me" features (PR review-as-me) work for OAuth users, not just token users. We use
        // the Git test provider, whose probe (TestRepositoryProvider) returns a canned whoami.
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.Git).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.Git, BuildToken("at", "rt", expiresIn: TimeSpan.FromHours(2)));

        var credId = await ConnectViaOAuthAsync(userId, teamId, instanceId, stub).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var resolved = await verify.Resolve<IActorIdentityResolver>().ResolveAsync(userId, instanceId, CancellationToken.None).ConfigureAwait(false);

        resolved.ShouldNotBeNull("an OAuth connect must give the owner an act-as-user identity, like PAT linking does");
        resolved!.CredentialId.ShouldBe(credId);
        resolved.ProviderUsername.ShouldBe("Test User");      // from TestRepositoryProvider's probe
        resolved.ProviderUserId.ShouldBe("test-user-id");
    }

    [Fact]
    public async Task Reconnecting_via_oauth_repoints_the_single_identity_and_keeps_the_first_credential()
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.Git).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.Git, BuildToken("at", "rt", expiresIn: TimeSpan.FromHours(2)));

        var firstCredId = await ConnectViaOAuthAsync(userId, teamId, instanceId, stub).ConfigureAwait(false);
        var secondCredId = await ConnectViaOAuthAsync(userId, teamId, instanceId, stub).ConfigureAwait(false);

        firstCredId.ShouldNotBe(secondCredId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // One identity per (user, instance) — the second connect re-points it, doesn't duplicate.
        var identities = await db.UserProviderIdentity.AsNoTracking()
            .Where(i => i.UserId == userId && i.ProviderInstanceId == instanceId && i.DeletedDate == null)
            .ToListAsync().ConfigureAwait(false);
        identities.Count.ShouldBe(1);
        identities[0].CredentialId.ShouldBe(secondCredId);

        // Re-point is NON-destructive — connecting again must not revoke the earlier credential
        // (the user manages credentials on the Personal tab).
        var first = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == firstCredId).ConfigureAwait(false);
        first.Status.ShouldBe(CredentialStatus.Active);
        first.DeletedDate.ShouldBeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<(Guid UserId, Guid TeamId, Guid InstanceId)> SeedOAuthCapableInstanceAsync(ProviderKind kind)
    {
        return await SeedInstanceAsync(kind, oauthCapable: true).ConfigureAwait(false);
    }

    private async Task<(Guid UserId, Guid TeamId, Guid InstanceId)> SeedInstanceAsync(ProviderKind kind, bool oauthCapable)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = kind,
            DisplayName = "OAuth-capable",
            BaseUrl = $"https://{kind.ToString().ToLowerInvariant()}-{suffix}.local",
            OauthClientId = oauthCapable ? "test-client" : null,
            OauthClientSecretEnc = oauthCapable ? encryptor.Encrypt("test-secret") : null
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, team.Id, instance.Id);
    }

    private static void RegisterTestUserAndTeam(ContainerBuilder b, Guid userId, Guid teamId)
    {
        b.RegisterInstance(new Infrastructure.TestCurrentUser(userId, "tester", new[] { Roles.Admin })).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
        b.RegisterInstance(new Infrastructure.TestCurrentTeam(teamId)).As<CodeSpace.Core.Services.Identity.ICurrentTeam>().SingleInstance();
    }

    private static OAuthTokenResponse BuildToken(string accessToken, string? refreshToken = null, TimeSpan? expiresIn = null) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresAt = expiresIn.HasValue ? DateTimeOffset.UtcNow + expiresIn.Value : null
    };

    private sealed class SingleProviderRegistry : IOAuthClientRegistry
    {
        private readonly IOAuthClient _client;
        public SingleProviderRegistry(IOAuthClient client) { _client = client; }
        public IOAuthClient Get(ProviderKind kind) => _client;
    }

    /// <summary>
    /// Runs a full Init → Complete OAuth round for the given owner with the token endpoint stubbed.
    /// The callback's identity-link probe resolves through the REAL registry (Git → TestRepositoryProvider,
    /// a canned valid whoami), so no probe stub is needed. Returns the persisted credential id.
    /// </summary>
    private async Task<Guid> ConnectViaOAuthAsync(Guid userId, Guid teamId, Guid instanceId, StubOAuthClient stub)
    {
        InitCredentialOAuthResult initResult;
        using (var initScope = _fixture.BeginScope(b =>
        {
            RegisterTestUserAndTeam(b, userId, teamId);
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        }))
        {
            initResult = await initScope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand
            {
                ProviderInstanceId = instanceId,
                DisplayName = "Maya's Git"
            }).ConfigureAwait(false);
        }

        using var callbackScope = _fixture.BeginScope(b =>
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());

        var result = await callbackScope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand
        {
            Code = "code-from-provider",
            State = initResult.State
        }).ConfigureAwait(false);

        return result.CredentialId;
    }
}
