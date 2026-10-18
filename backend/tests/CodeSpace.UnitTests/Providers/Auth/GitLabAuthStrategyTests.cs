using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.GitLab.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Auth;

[Trait("Category", "Unit")]
public class GitLabAuthStrategyTests
{
    [Fact]
    public async Task GitLabPat_returns_pat_token()
    {
        var strategy = new GitLabPatAuthStrategy(new FakeCredentialResolver(new PatPayload { Token = "glpat_xyz" }));

        var result = await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.Pat), CancellationToken.None);

        result.Token.ShouldBe("glpat_xyz");
    }

    [Fact]
    public async Task GitLabProjectAccessToken_returns_project_token()
    {
        var strategy = new GitLabProjectAccessTokenStrategy(new FakeCredentialResolver(new ProjectAccessTokenPayload { Token = "proj-token" }));

        var result = await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.ProjectAccessToken), CancellationToken.None);

        result.Token.ShouldBe("proj-token");
    }

    [Fact]
    public async Task GitLabGroupAccessToken_returns_group_token()
    {
        var strategy = new GitLabGroupAccessTokenStrategy(new FakeCredentialResolver(new GroupAccessTokenPayload { Token = "group-token" }));

        var result = await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.GroupAccessToken), CancellationToken.None);

        result.Token.ShouldBe("group-token");
    }

    [Fact]
    public async Task GitLabOAuth_returns_access_token()
    {
        var expires = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var strategy = new GitLabOAuthAuthStrategy(new FakeCredentialResolver(new OAuthPayload { AccessToken = "glat_xyz", ExpiresAt = expires }), new NoopOAuthTokenRefresher(), new NoopOAuthTokenRefresher());

        var result = await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth), CancellationToken.None);

        result.Token.ShouldBe("glat_xyz");
        result.ExpiresAt.ShouldBe(expires);
    }

    [Fact]
    public async Task GitLabOAuth_hands_back_the_token_recovery_found_and_nothing_when_it_found_none()
    {
        var expires = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var context = AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth);
        var found = new GitLabOAuthAuthStrategy(new FakeCredentialResolver(new OAuthPayload { AccessToken = "unused" }), new NoopOAuthTokenRefresher(), new FixedRecovery(new OAuthPayload { AccessToken = "glat_replacement", ExpiresAt = expires }));
        var none = new GitLabOAuthAuthStrategy(new FakeCredentialResolver(new OAuthPayload { AccessToken = "unused" }), new NoopOAuthTokenRefresher(), new FixedRecovery(null));

        var replacement = await found.InvalidateAsync(context, "glat_refused", CancellationToken.None);

        replacement.ShouldNotBeNull().Token.ShouldBe("glat_replacement");
        replacement.ExpiresAt.ShouldBe(expires);
        (await none.InvalidateAsync(context, "glat_refused", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task GitLabOAuth_stores_the_pair_its_process_kept_before_reading_a_credential_another_process_marked_refused()
    {
        var context = AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth);
        context.Credential.Status = CredentialStatus.Error;
        context.Credential.LastError = "needs reconnect: invalid_grant";
        var holding = new HoldingKeptPair();
        var strategy = new GitLabOAuthAuthStrategy(new ErrorFailingCredentialResolver(new OAuthPayload { AccessToken = "glat_kept" }), holding, holding);

        var result = await strategy.ResolveAsync(context, CancellationToken.None);

        result.Token.ShouldBe("glat_kept", "a live pair this process holds outranks the verdict a refusal of the consumed one wrote on the row");
        holding.Stores.ShouldBe(1);
    }

    [Fact]
    public async Task GitLabOAuth_still_fails_fast_on_a_refused_credential_when_it_holds_no_pair()
    {
        var context = AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth);
        context.Credential.Status = CredentialStatus.Error;
        var strategy = new GitLabOAuthAuthStrategy(new ErrorFailingCredentialResolver(new OAuthPayload { AccessToken = "unused" }), new NoopOAuthTokenRefresher(), new NoopOAuthTokenRefresher());

        await Should.ThrowAsync<CodeSpace.Messages.Exceptions.CredentialNeedsReconnectException>(() => strategy.ResolveAsync(context, CancellationToken.None));
    }

    [Fact]
    public void Only_the_oauth_strategy_can_recover_a_token_the_static_ones_have_nothing_to_replace_it_with()
    {
        var resolver = new FakeCredentialResolver(new PatPayload { Token = "t" });

        (new GitLabOAuthAuthStrategy(resolver, new NoopOAuthTokenRefresher(), new NoopOAuthTokenRefresher()) is IRecoverableProviderAuthStrategy).ShouldBeTrue();
        (new GitLabPatAuthStrategy(resolver) is IRecoverableProviderAuthStrategy).ShouldBeFalse();
        (new GitLabProjectAccessTokenStrategy(resolver) is IRecoverableProviderAuthStrategy).ShouldBeFalse();
        (new GitLabGroupAccessTokenStrategy(resolver) is IRecoverableProviderAuthStrategy).ShouldBeFalse();
    }

    private sealed class FixedRecovery : IOAuthTokenRecovery
    {
        private readonly OAuthPayload? _payload;

        public FixedRecovery(OAuthPayload? payload) { _payload = payload; }

        public Task<OAuthPayload?> RecoverAfterRejectionAsync(ProviderContext context, string rejectedAccessToken, CancellationToken cancellationToken) => Task.FromResult(_payload);

        public Task StoreKeptPairAsync(ProviderContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("Pat")]
    [InlineData("ProjectAccessToken")]
    [InlineData("GroupAccessToken")]
    [InlineData("OAuth")]
    public async Task Each_strategy_throws_when_payload_type_does_not_match(string strategyName)
    {
        var wrongPayload = new BasicAuthPayload { Username = "u", Password = "p" };
        var resolver = new FakeCredentialResolver(wrongPayload);

        Func<Task> act = strategyName switch
        {
            "Pat" => async () => await new GitLabPatAuthStrategy(resolver).ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.Pat), CancellationToken.None),
            "ProjectAccessToken" => async () => await new GitLabProjectAccessTokenStrategy(resolver).ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.ProjectAccessToken), CancellationToken.None),
            "GroupAccessToken" => async () => await new GitLabGroupAccessTokenStrategy(resolver).ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.GroupAccessToken), CancellationToken.None),
            "OAuth" => async () => await new GitLabOAuthAuthStrategy(resolver, new NoopOAuthTokenRefresher(), new NoopOAuthTokenRefresher()).ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth), CancellationToken.None),
            _ => throw new ArgumentException(strategyName)
        };

        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void All_GitLab_strategies_declare_GitLab_kind()
    {
        var resolver = new FakeCredentialResolver(new PatPayload { Token = "t" });
        new GitLabPatAuthStrategy(resolver).Kind.ShouldBe(ProviderKind.GitLab);
        new GitLabProjectAccessTokenStrategy(resolver).Kind.ShouldBe(ProviderKind.GitLab);
        new GitLabGroupAccessTokenStrategy(resolver).Kind.ShouldBe(ProviderKind.GitLab);
        new GitLabOAuthAuthStrategy(resolver, new NoopOAuthTokenRefresher(), new NoopOAuthTokenRefresher()).Kind.ShouldBe(ProviderKind.GitLab);
    }

    [Fact]
    public void GitLab_strategies_declare_distinct_auth_types()
    {
        var resolver = new FakeCredentialResolver(new PatPayload { Token = "t" });
        var authTypes = new[]
        {
            new GitLabPatAuthStrategy(resolver).AuthType,
            new GitLabProjectAccessTokenStrategy(resolver).AuthType,
            new GitLabGroupAccessTokenStrategy(resolver).AuthType,
            new GitLabOAuthAuthStrategy(resolver, new NoopOAuthTokenRefresher(), new NoopOAuthTokenRefresher()).AuthType
        };

        authTypes.Distinct().Count().ShouldBe(4);
    }
}
