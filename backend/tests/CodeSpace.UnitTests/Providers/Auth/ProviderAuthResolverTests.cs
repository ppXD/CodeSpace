using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Auth;

[Trait("Category", "Unit")]
public class ProviderAuthResolverTests
{
    [Fact]
    public async Task ResolveAsync_dispatches_to_strategy_matching_kind_and_auth_type()
    {
        var resolver = new ProviderAuthResolver(new IProviderAuthStrategy[]
        {
            new StubStrategy(ProviderKind.GitHub, AuthType.Pat, "github-pat-token"),
            new StubStrategy(ProviderKind.GitLab, AuthType.Pat, "gitlab-pat-token")
        });

        var auth = await resolver.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.Pat), CancellationToken.None);

        auth.Token.ShouldBe("gitlab-pat-token");
    }

    [Fact]
    public async Task ResolveAsync_throws_NotSupportedException_when_no_strategy_registered()
    {
        var resolver = new ProviderAuthResolver(new IProviderAuthStrategy[]
        {
            new StubStrategy(ProviderKind.GitHub, AuthType.Pat, "token")
        });

        var act = async () => await resolver.ResolveAsync(AuthTestContextFactory.Build(ProviderKind.GitHub, AuthType.OAuth), CancellationToken.None);

        var ex = await act.ShouldThrowAsync<NotSupportedException>();
        ex.Message.ShouldContain("GitHub");
        ex.Message.ShouldContain("OAuth");
    }

    [Fact]
    public void Constructor_throws_when_two_strategies_claim_same_kind_and_auth_type()
    {
        var act = () => new ProviderAuthResolver(new IProviderAuthStrategy[]
        {
            new StubStrategy(ProviderKind.GitHub, AuthType.Pat, "a"),
            new StubStrategy(ProviderKind.GitHub, AuthType.Pat, "b")
        });

        act.ShouldThrow<ArgumentException>();
    }

    private sealed class StubStrategy : IProviderAuthStrategy
    {
        private readonly string _token;

        public StubStrategy(ProviderKind kind, AuthType authType, string token)
        {
            Kind = kind;
            AuthType = authType;
            _token = token;
        }

        public ProviderKind Kind { get; }
        public AuthType AuthType { get; }

        public Task<ResolvedAuth> ResolveAsync(CodeSpace.Core.Services.Providers.ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = _token });
    }
}
