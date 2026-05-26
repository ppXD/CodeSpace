using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

public class OAuthClientRegistryTests
{
    [Fact]
    public void Get_returns_the_client_registered_for_the_kind()
    {
        var github = new StubOAuthClient(ProviderKind.GitHub);
        var gitlab = new StubOAuthClient(ProviderKind.GitLab);

        var registry = new OAuthClientRegistry(new IOAuthClient[] { github, gitlab });

        registry.Get(ProviderKind.GitHub).ShouldBeSameAs(github);
        registry.Get(ProviderKind.GitLab).ShouldBeSameAs(gitlab);
    }

    [Fact]
    public void Get_throws_when_kind_has_no_registered_client()
    {
        var registry = new OAuthClientRegistry(new IOAuthClient[] { new StubOAuthClient(ProviderKind.GitHub) });

        Should.Throw<NotSupportedException>(() => registry.Get(ProviderKind.GitLab));
    }

    private sealed class StubOAuthClient : IOAuthClient
    {
        public StubOAuthClient(ProviderKind kind) { Kind = kind; }

        public ProviderKind Kind { get; }

        public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => throw new NotSupportedException();
        public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
