using CodeSpace.Core.Services.Providers.GitHub.Auth;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Auth;

public class GitHubAuthStrategyTests
{
    [Fact]
    public async Task GitHubPat_returns_pat_token()
    {
        var strategy = new GitHubPatAuthStrategy(new FakeCredentialResolver(new PatPayload { Token = "ghp_xyz" }));

        var result = await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitHub, AuthType.Pat), CancellationToken.None);

        result.Token.ShouldBe("ghp_xyz");
        result.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task GitHubPat_throws_when_payload_type_does_not_match()
    {
        var strategy = new GitHubPatAuthStrategy(new FakeCredentialResolver(new OAuthPayload { AccessToken = "wrong" }));

        var act = async () => await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitHub, AuthType.Pat), CancellationToken.None);

        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("PatPayload");
    }

    [Fact]
    public async Task GitHubOAuth_returns_access_token_and_expiry()
    {
        var expires = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var strategy = new GitHubOAuthAuthStrategy(new FakeCredentialResolver(new OAuthPayload { AccessToken = "ghu_abc", ExpiresAt = expires }), new NoopOAuthTokenRefresher());

        var result = await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitHub, AuthType.OAuth), CancellationToken.None);

        result.Token.ShouldBe("ghu_abc");
        result.ExpiresAt.ShouldBe(expires);
    }

    [Fact]
    public async Task GitHubOAuth_throws_when_payload_type_does_not_match()
    {
        var strategy = new GitHubOAuthAuthStrategy(new FakeCredentialResolver(new PatPayload { Token = "wrong" }), new NoopOAuthTokenRefresher());

        var act = async () => await strategy.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitHub, AuthType.OAuth), CancellationToken.None);

        var ex = await act.ShouldThrowAsync<InvalidOperationException>();
        ex.Message.ShouldContain("OAuthPayload");
    }

    [Fact]
    public void GitHubPat_declares_correct_kind_and_authtype()
    {
        var strategy = new GitHubPatAuthStrategy(new FakeCredentialResolver(new PatPayload { Token = "t" }));

        strategy.Kind.ShouldBe(ProviderKind.GitHub);
        strategy.AuthType.ShouldBe(AuthType.Pat);
    }

    [Fact]
    public void GitHubOAuth_declares_correct_kind_and_authtype()
    {
        var strategy = new GitHubOAuthAuthStrategy(new FakeCredentialResolver(new OAuthPayload { AccessToken = "t" }), new NoopOAuthTokenRefresher());

        strategy.Kind.ShouldBe(ProviderKind.GitHub);
        strategy.AuthType.ShouldBe(AuthType.OAuth);
    }
}
