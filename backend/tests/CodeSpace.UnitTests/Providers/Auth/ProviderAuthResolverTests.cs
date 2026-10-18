using CodeSpace.Core.Services.Providers;
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

    // ─── After a provider answered 401 to the token it was handed ───

    [Fact]
    public async Task InvalidateAsync_asks_the_strategy_that_can_recover_and_remembers_the_replacement_so_an_echo_of_it_is_struck()
    {
        var resolver = new ProviderAuthResolver(new IProviderAuthStrategy[] { new RecoverableStrategy(ProviderKind.GitLab, AuthType.OAuth, replacement: "glat-REPLACEMENT-0123456789") });
        var context = AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth);

        var replacement = await resolver.InvalidateAsync(context, "glat-refused", CancellationToken.None);

        replacement.ShouldNotBeNull().Token.ShouldBe("glat-REPLACEMENT-0123456789");
        PresentedProviderCredentials.RedactorFor(context.Instance).Redact("the provider said glat-REPLACEMENT-0123456789 was refused").ShouldNotContain("glat-REPLACEMENT-0123456789");
    }

    [Fact]
    public async Task InvalidateAsync_answers_null_when_the_strategy_has_nothing_to_replace_the_token_with()
    {
        var resolver = new ProviderAuthResolver(new IProviderAuthStrategy[] { new RecoverableStrategy(ProviderKind.GitLab, AuthType.OAuth, replacement: null) });

        (await resolver.InvalidateAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.OAuth), "glat-refused", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task InvalidateAsync_answers_null_for_a_strategy_that_cannot_recover_a_static_token_is_the_same_token_whenever_it_is_asked_for()
    {
        var resolver = new ProviderAuthResolver(new IProviderAuthStrategy[] { new StubStrategy(ProviderKind.GitLab, AuthType.Pat, "glpat-token") });

        (await resolver.InvalidateAsync(AuthTestContextFactory.Build(ProviderKind.GitLab, AuthType.Pat), "glpat-token", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task InvalidateAsync_answers_null_rather_than_throwing_when_no_strategy_is_registered()
    {
        var resolver = new ProviderAuthResolver(new IProviderAuthStrategy[] { new StubStrategy(ProviderKind.GitHub, AuthType.Pat, "token") });

        (await resolver.InvalidateAsync(AuthTestContextFactory.Build(ProviderKind.GitHub, AuthType.OAuth), "token", CancellationToken.None)).ShouldBeNull("the caller surfaces the 401 it already has; it is not owed a second failure for asking");
    }

    private sealed class RecoverableStrategy : IRecoverableProviderAuthStrategy
    {
        private readonly string? _replacement;

        public RecoverableStrategy(ProviderKind kind, AuthType authType, string? replacement)
        {
            Kind = kind;
            AuthType = authType;
            _replacement = replacement;
        }

        public ProviderKind Kind { get; }
        public AuthType AuthType { get; }

        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "unused" });

        public Task<ResolvedAuth?> InvalidateAsync(ProviderContext context, string rejectedToken, CancellationToken cancellationToken) =>
            Task.FromResult(_replacement is null ? null : new ResolvedAuth { Token = _replacement });
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
