using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Settings.Application;
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
/// Credential row persisted, state consumed. Also pins replay rejection, cross-team
/// rejection at the init boundary, the binding of a flow to the browser that started it,
/// and the return-address policy at both ends.
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
        var returnUrl = $"{scope.Resolve<PublicBaseUrlSetting>().Value}/settings/credentials";
        var result = await mediator.Send(new InitCredentialOAuthCommand
        {
            ProviderInstanceId = instanceId,
            DisplayName = "Maya's GitLab",
            ReturnUrl = returnUrl
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
        row.ReturnUrl.ShouldBe(returnUrl);
        row.CodeVerifier.ShouldNotBeNullOrEmpty();

        // Only the hash of the starting browser's nonce is stored — a database read never yields a value that completes the flow.
        result.BrowserNonce.ShouldNotBeNullOrEmpty();
        row.BrowserNonceHash.ShouldNotBeNull().ShouldBe(OAuthBrowserNonce.Hash(result.BrowserNonce));
        row.BrowserNonceHash.ShouldNotContain(result.BrowserNonce);
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
                State = initResult.State,
                BrowserNonce = initResult.BrowserNonce
            }).ConfigureAwait(false);
        }

        completeResult.TeamId.ShouldBe(teamId);
        var credentialId = completeResult.CredentialId.ShouldNotBeNull();
        completeResult.ReturnUrl.ShouldBe("/");

        // Verify the Credential row was persisted with the right shape, and that the
        // pending state was consumed.
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var cred = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
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
            await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = "c1", State = initResult.State, BrowserNonce = initResult.BrowserNonce }).ConfigureAwait(false);
        }

        // Second callback with the same state must fail — proves one-time-use enforcement.
        using (var scope = _fixture.BeginScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            var act = async () => await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = "c2", State = initResult.State, BrowserNonce = initResult.BrowserNonce }).ConfigureAwait(false);
            await act.ShouldThrowAsync<OAuthCallbackException>().ConfigureAwait(false);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("a-nonce-this-browser-never-received")]
    public async Task Complete_from_a_browser_that_did_not_start_the_flow_consumes_nothing_and_is_handed_to_the_spa(string? browserNonce)
    {
        // The capture this pins: an admin of one team starts a connect on their own OAuth app and forwards the
        // authorize link; whoever's browser finishes it used to hand that team their provider token. Without the
        // starting browser's nonce, nothing is consumed, exchanged or persisted — the callback is only pointed at the
        // SPA's completion page, where the starter alone can finish it — and the starter can still finish.
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("victim-token"));
        var initResult = await InitAsync(userId, teamId, instanceId, stub, returnUrl: null).ConfigureAwait(false);

        using (var scope = _fixture.BeginScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance()))
        {
            var handedOff = await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = "victim-code", State = initResult.State, BrowserNonce = browserNonce }).ConfigureAwait(false);

            handedOff.CredentialId.ShouldBeNull("a callback without the starting browser's nonce completes nothing");
            handedOff.ReturnUrl.ShouldBe(OAuthReturnUrl.CompletionPage, "handed to the SPA's completion page on this site");
        }

        stub.LastExchange.ShouldBeNull("the code must never reach the token endpoint");

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.Credential.AsNoTracking().AnyAsync(c => c.ProviderInstanceId == instanceId).ConfigureAwait(false)).ShouldBeFalse("no credential lands in the starter's team");
            (await db.OAuthPendingState.AsNoTracking().AnyAsync(s => s.State == initResult.State).ConfigureAwait(false)).ShouldBeTrue("a refused callback consumes nothing");
        }

        using var starter = _fixture.BeginScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());
        var completed = await starter.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = "own-code", State = initResult.State, BrowserNonce = initResult.BrowserNonce }).ConfigureAwait(false);
        completed.TeamId.ShouldBe(teamId, "the browser that started the connect still finishes it");
    }

    [Theory]
    [InlineData("https://attacker.example/landing")]
    [InlineData("//attacker.example/x")]
    [InlineData("javascript:alert(1)")]
    public async Task Init_refuses_a_return_address_off_this_site_and_starts_nothing(string returnUrl)
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));

        var act = async () => await InitAsync(userId, teamId, instanceId, stub, returnUrl).ConfigureAwait(false);

        var refused = await act.ShouldThrowAsync<ArgumentException>().ConfigureAwait(false);
        refused.ParamName.ShouldBe("returnUrl");

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().OAuthPendingState.AsNoTracking().AnyAsync(s => s.ProviderInstanceId == instanceId).ConfigureAwait(false)).ShouldBeFalse("a refused start leaves no pending connect");
    }

    [Fact]
    public async Task Complete_lands_on_root_when_a_pending_return_address_no_longer_passes()
    {
        // Checked again at the callback rather than trusted from init: a row can outlive a change to the public
        // origin, and a row written before the policy existed carries whatever its starter sent.
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));
        var nonce = OAuthBrowserNonce.Mint();
        var state = $"legacy-{Guid.NewGuid():N}";

        using (var seed = _fixture.BeginScope())
        {
            var db = seed.Resolve<CodeSpaceDbContext>();
            db.OAuthPendingState.Add(new OAuthPendingState { State = state, ProviderInstanceId = instanceId, TeamId = teamId, InitiatorUserId = userId, IntendedOwnerUserId = userId, CodeVerifier = "verifier", IntendedDisplayName = "legacy", ReturnUrl = "https://elsewhere.example/landing", BrowserNonceHash = OAuthBrowserNonce.Hash(nonce), ExpiresDate = DateTimeOffset.UtcNow.AddMinutes(5) });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        using var scope = _fixture.BeginScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());
        var result = await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = "c", State = state, BrowserNonce = nonce }).ConfigureAwait(false);

        result.ReturnUrl.ShouldBe(OAuthReturnUrl.Fallback);
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

    // ── A callback without the binding cookie: handed to the SPA, finished by the starter signed in ─────────

    [Fact]
    public async Task A_handed_off_connect_is_finished_by_the_person_who_started_it()
    {
        // The SPA and the API on different sites: the browser never keeps the binding cookie, so every callback arrives
        // without it. The SPA's completion page then finishes the connect as its signed-in starter.
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("access-from-provider", "refresh-from-provider"));
        var init = await InitAsync(userId, teamId, instanceId, stub, returnUrl: "http://127.0.0.1:5180/oauth-callback").ConfigureAwait(false);

        var handedOff = await CallbackAsync(init.State, "code-from-provider", browserNonce: null, stub).ConfigureAwait(false);
        handedOff.ReturnUrl.ShouldBe("http://127.0.0.1:5180/oauth-callback", "back to the SPA origin that started it");

        var completed = await CompleteAsInitiatorAsync(userId, init.State, "code-from-provider", stub).ConfigureAwait(false);

        var credentialId = completed.CredentialId.ShouldNotBeNull();
        completed.TeamId.ShouldBe(teamId);
        stub.LastExchange.ShouldNotBeNull().Code.ShouldBe("code-from-provider");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var cred = await db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);

        cred.TeamId.ShouldBe(teamId);
        cred.OwnerUserId.ShouldBe(userId);
        cred.CreatedBy.ShouldBe(userId);
        ((OAuthPayload)verify.Resolve<ICredentialPayloadSerializer>().Deserialize(AuthType.OAuth, verify.Resolve<IPayloadEncryptor>().Decrypt(cred.EncryptedPayload))).AccessToken.ShouldBe("access-from-provider");
        (await db.OAuthPendingState.AsNoTracking().AnyAsync(s => s.State == init.State).ConfigureAwait(false)).ShouldBeFalse("finishing spends the state");
    }

    [Fact]
    public async Task Nobody_but_the_starter_finishes_a_handed_off_connect_and_the_starter_still_can()
    {
        // The forwarded-link capture again, one step further: the victim's browser was handed to the SPA, and the
        // victim is signed in there — in the very team, as an Owner. Their session is not the starter's, so nothing is
        // consumed, exchanged or persisted, and the connect stays the starter's to finish.
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var victimId = await SeedMemberAsync(teamId, TeamRole.Owner).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("victim-token"));
        var init = await InitAsync(userId, teamId, instanceId, stub, returnUrl: null).ConfigureAwait(false);

        var act = async () => await CompleteAsInitiatorAsync(victimId, init.State, "victim-code", stub).ConfigureAwait(false);

        var refused = await act.ShouldThrowAsync<OAuthCallbackException>().ConfigureAwait(false);
        refused.Reason.ShouldContain("not started by you");
        stub.LastExchange.ShouldBeNull("the victim's code must never reach the token endpoint");

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.Credential.AsNoTracking().AnyAsync(c => c.ProviderInstanceId == instanceId).ConfigureAwait(false)).ShouldBeFalse();
            (await db.OAuthPendingState.AsNoTracking().AnyAsync(s => s.State == init.State).ConfigureAwait(false)).ShouldBeTrue("a refused finish consumes nothing");
        }

        (await CompleteAsInitiatorAsync(userId, init.State, "own-code", stub).ConfigureAwait(false)).CredentialId.ShouldNotBeNull("the starter still finishes it");
    }

    [Fact]
    public async Task A_handed_off_connect_is_not_finished_without_a_session()
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));
        var init = await InitAsync(userId, teamId, instanceId, stub, returnUrl: null).ConfigureAwait(false);

        using var anonymous = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new Infrastructure.TestCurrentUser(null)).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        });

        var act = async () => await anonymous.Resolve<IMediator>().Send(new CompleteCredentialOAuthAsInitiatorCommand { State = init.State, Code = "code" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<UnauthorizedAccessException>().ConfigureAwait(false);
        stub.LastExchange.ShouldBeNull();
    }

    [Fact]
    public async Task Either_proof_spends_the_state_for_both()
    {
        // One connect, one credential: finishing it through the SPA refuses the cookie, and the cookie refuses the SPA.
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));

        var viaSpa = await InitAsync(userId, teamId, instanceId, stub, returnUrl: null).ConfigureAwait(false);
        await CompleteAsInitiatorAsync(userId, viaSpa.State, "c1", stub).ConfigureAwait(false);
        await Should.ThrowAsync<OAuthCallbackException>(async () => await CallbackAsync(viaSpa.State, "c2", viaSpa.BrowserNonce, stub).ConfigureAwait(false)).ConfigureAwait(false);

        var viaCookie = await InitAsync(userId, teamId, instanceId, stub, returnUrl: null).ConfigureAwait(false);
        (await CallbackAsync(viaCookie.State, "c3", viaCookie.BrowserNonce, stub).ConfigureAwait(false)).CredentialId.ShouldNotBeNull();
        await Should.ThrowAsync<OAuthCallbackException>(async () => await CompleteAsInitiatorAsync(userId, viaCookie.State, "c4", stub).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5180/settings/credentials", "http://127.0.0.1:5180/oauth-callback")]
    [InlineData("/settings/credentials", "/oauth-callback")]
    public async Task A_handed_off_connect_lands_on_the_completion_page_of_the_origin_it_named(string returnUrl, string completionPage)
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));
        var init = await InitAsync(userId, teamId, instanceId, stub, returnUrl).ConfigureAwait(false);

        (await CallbackAsync(init.State, "c", browserNonce: null, stub).ConfigureAwait(false)).ReturnUrl.ShouldBe(completionPage);
    }

    [Fact]
    public async Task An_expired_connect_is_refused_rather_than_handed_off()
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));
        var state = $"expired-{Guid.NewGuid():N}";

        await InsertStateAsAnOlderBuildAsync(state, instanceId, teamId, userId, OAuthBrowserNonce.Hash(OAuthBrowserNonce.Mint()), DateTimeOffset.UtcNow.AddMinutes(-1)).ConfigureAwait(false);

        var refused = await Should.ThrowAsync<OAuthCallbackException>(async () => await CallbackAsync(state, "c", browserNonce: null, stub).ConfigureAwait(false)).ConfigureAwait(false);
        refused.Reason.ShouldContain("expired");

        // Not left for the deployment-wide expiry sweep, whose tests share this database.
        using var cleanup = _fixture.BeginScope();
        await cleanup.Resolve<CodeSpaceDbContext>().OAuthPendingState.Where(s => s.State == state).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    // ── Mixed versions: rows either build writes are finished by the other ──────────────────────────────────

    /// <summary>
    /// A rolling deploy puts the start of a connect and its callback on different builds. A row written by the previous
    /// build (with the nonce hash, #2104) or by one before migration 0246 (without it) — inserted here with exactly the
    /// columns that build writes — is handed off by this one and finished by its starter. This build adds no column, so
    /// nothing a previous build leaves out can make a row unreadable here.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_connect_an_older_build_started_is_finished_here_by_its_starter(bool olderBuildBindsTheBrowser)
    {
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));
        var state = $"older-build-{Guid.NewGuid():N}";

        await InsertStateAsAnOlderBuildAsync(state, instanceId, teamId, userId, olderBuildBindsTheBrowser ? OAuthBrowserNonce.Hash(OAuthBrowserNonce.Mint()) : null, DateTimeOffset.UtcNow.AddMinutes(5)).ConfigureAwait(false);

        (await CallbackAsync(state, "c", browserNonce: null, stub).ConfigureAwait(false)).CredentialId.ShouldBeNull("handed off, not refused");
        (await CompleteAsInitiatorAsync(userId, state, "c", stub).ConfigureAwait(false)).CredentialId.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_connect_this_build_started_matches_the_previous_builds_callback_lookup()
    {
        // The previous build's callback consumes `state = $1 AND browser_nonce_hash = $2` (OAuthStateStore at #2104).
        // Run verbatim against a row this build wrote: an older pod that receives the callback still finishes it.
        var (userId, teamId, instanceId) = await SeedOAuthCapableInstanceAsync(ProviderKind.GitLab).ConfigureAwait(false);
        var stub = new StubOAuthClient(ProviderKind.GitLab, BuildToken("at"));
        var init = await InitAsync(userId, teamId, instanceId, stub, returnUrl: "/oauth-callback").ConfigureAwait(false);
        var hash = OAuthBrowserNonce.Hash(init.BrowserNonce);

        using var verify = _fixture.BeginScope();
        var matches = await verify.Resolve<CodeSpaceDbContext>().Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM oauth_pending_state WHERE state = {init.State} AND browser_nonce_hash = {hash}").SingleAsync().ConfigureAwait(false);

        matches.ShouldBe(1);
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
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };

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
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = user.Id, Role = TeamRole.Owner });
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (user.Id, team.Id, instance.Id);
    }

    private async Task<InitCredentialOAuthResult> InitAsync(Guid userId, Guid teamId, Guid instanceId, StubOAuthClient stub, string? returnUrl)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            RegisterTestUserAndTeam(b, userId, teamId);
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        });

        return await scope.Resolve<IMediator>().Send(new InitCredentialOAuthCommand { ProviderInstanceId = instanceId, DisplayName = "connect", ReturnUrl = returnUrl }).ConfigureAwait(false);
    }

    private async Task<CompleteCredentialOAuthResult> CallbackAsync(string state, string code, string? browserNonce, StubOAuthClient stub)
    {
        using var scope = _fixture.BeginScope(b => b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance());

        return await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthCommand { Code = code, State = state, BrowserNonce = browserNonce }).ConfigureAwait(false);
    }

    /// <summary>The SPA's completion page posting the handed-off code as <paramref name="callerId"/> — signed in, with no team header in play.</summary>
    private async Task<CompleteCredentialOAuthResult> CompleteAsInitiatorAsync(Guid callerId, string state, string code, StubOAuthClient stub)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new Infrastructure.TestCurrentUser(callerId, "caller")).As<CodeSpace.Core.Services.Identity.ICurrentUser>().SingleInstance();
            b.RegisterInstance<IOAuthClientRegistry>(new SingleProviderRegistry(stub)).SingleInstance();
        });

        return await scope.Resolve<IMediator>().Send(new CompleteCredentialOAuthAsInitiatorCommand { State = state, Code = code }).ConfigureAwait(false);
    }

    private async Task<Guid> SeedMemberAsync(Guid teamId, TeamRole role)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = Guid.NewGuid(), Email = $"m-{Guid.NewGuid():N}@x", Name = $"member-{Guid.NewGuid():N}" };

        db.User.Add(user);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync().ConfigureAwait(false);

        return user.Id;
    }

    /// <summary>
    /// A pending state as an older build writes it: the columns of 0005 plus, from #2104 on, browser_nonce_hash — and
    /// nothing this build might add (requested_scopes is left NULL, as a connect with default scopes stores it). Raw SQL
    /// so a new required column could not be filled in by the entity on its behalf.
    /// </summary>
    private async Task InsertStateAsAnOlderBuildAsync(string state, Guid instanceId, Guid teamId, Guid initiatorId, string? browserNonceHash, DateTimeOffset expires)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        if (browserNonceHash == null)
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO oauth_pending_state (state, provider_instance_id, team_id, initiator_user_id, code_verifier, intended_display_name, intended_owner_user_id, return_url, expires_date, created_date, created_by, last_modified_date, last_modified_by) VALUES ({state}, {instanceId}, {teamId}, {initiatorId}, {"verifier"}, {"older build"}, {initiatorId}, {"/oauth-callback"}, {expires}, {now}, {initiatorId}, {now}, {initiatorId})").ConfigureAwait(false);
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO oauth_pending_state (state, provider_instance_id, team_id, initiator_user_id, code_verifier, intended_display_name, intended_owner_user_id, return_url, expires_date, created_date, created_by, last_modified_date, last_modified_by, browser_nonce_hash) VALUES ({state}, {instanceId}, {teamId}, {initiatorId}, {"verifier"}, {"older build"}, {initiatorId}, {"/oauth-callback"}, {expires}, {now}, {initiatorId}, {now}, {initiatorId}, {browserNonceHash})").ConfigureAwait(false);
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
            State = initResult.State,
            BrowserNonce = initResult.BrowserNonce
        }).ConfigureAwait(false);

        return result.CredentialId.ShouldNotBeNull();
    }
}
