using CodeSpace.Core.Services.Providers.GitLab.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Auth;

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
        var strategy = new GitLabOAuthAuthStrategy(new FakeCredentialResolver(new OAuthPayload { AccessToken = "glat_xyz", ExpiresAt = expires }), new NoopOAuthTokenRefresher());

        var result = await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth), CancellationToken.None);

        result.Token.ShouldBe("glat_xyz");
        result.ExpiresAt.ShouldBe(expires);
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
            "OAuth" => async () => await new GitLabOAuthAuthStrategy(resolver, new NoopOAuthTokenRefresher()).ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth), CancellationToken.None),
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
        new GitLabOAuthAuthStrategy(resolver, new NoopOAuthTokenRefresher()).Kind.ShouldBe(ProviderKind.GitLab);
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
            new GitLabOAuthAuthStrategy(resolver, new NoopOAuthTokenRefresher()).AuthType
        };

        authTypes.Distinct().Count().ShouldBe(4);
    }
}
