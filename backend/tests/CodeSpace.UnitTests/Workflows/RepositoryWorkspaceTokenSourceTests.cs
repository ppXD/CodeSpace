using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="RepositoryWorkspaceTokenSource"/> is the one place a workspace's credential is asked for again, so it must ask
/// the way the provisioning resolve asked — for the repository and team it was given, through the scoped resolver — and keep
/// nothing between asks: one DI scope per ask (a long-lived scope's DbContext would hand back the very credential row this
/// exists to re-read), disposed when the ask ends. The real resolver, refresher and database behind it are composed in the
/// integration tests.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RepositoryWorkspaceTokenSourceTests
{
    [Fact]
    public async Task It_asks_the_resolver_for_the_repository_and_team_it_was_given_and_returns_its_token()
    {
        var repositoryId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var resolver = new FakeResolver(Request(token: "the-token"));
        var source = new RepositoryWorkspaceTokenSource(new CountingScopes(resolver));

        (await source.ResolveAsync(repositoryId, teamId, CancellationToken.None)).ShouldBe("the-token");

        resolver.Asked.ShouldBe(new[] { (repositoryId, teamId) });
    }

    [Fact]
    public async Task It_answers_null_for_a_request_with_no_token_an_anonymous_repository()
    {
        var source = new RepositoryWorkspaceTokenSource(new CountingScopes(new FakeResolver(Request(token: null))));

        (await source.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task It_opens_one_scope_per_ask_and_disposes_it_whether_the_ask_succeeds_or_fails()
    {
        var scopes = new CountingScopes(new FakeResolver(Request(token: "t")));
        var source = new RepositoryWorkspaceTokenSource(scopes);

        await source.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        await source.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        scopes.Created.ShouldBe(2, "nothing is kept between asks");
        scopes.Disposed.ShouldBe(2);

        var failing = new CountingScopes(new FakeResolver(new WorkspaceException("Repository not found for this team.")));

        await Should.ThrowAsync<WorkspaceException>(() => new RepositoryWorkspaceTokenSource(failing).ResolveAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));

        failing.Disposed.ShouldBe(1, "a failing ask releases its scope too");
    }

    private static WorkspaceRequest Request(string? token) => new() { RepositoryUrl = "https://example.test/repo.git", Token = token };

    private sealed class FakeResolver : IAgentWorkspaceResolver
    {
        private readonly WorkspaceRequest? _request;
        private readonly Exception? _failure;

        public FakeResolver(WorkspaceRequest request) { _request = request; }
        public FakeResolver(Exception failure) { _failure = failure; }

        public List<(Guid RepositoryId, Guid TeamId)> Asked { get; } = new();

        public Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken, string? @ref = null, bool softFallback = false, string? pinnedSha = null)
        {
            Asked.Add((repositoryId, teamId));

            return _failure is not null ? throw _failure : Task.FromResult(_request);
        }

        public Task<WorkspaceProvisionRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException("the token source asks by repository id");
    }

    /// <summary>Hands out a scope whose provider serves the resolver, counting scopes created and disposed.</summary>
    private sealed class CountingScopes(IAgentWorkspaceResolver resolver) : IServiceScopeFactory
    {
        public int Created { get; private set; }
        public int Disposed { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;

            return new Scope(new ServiceCollection().AddSingleton(resolver).BuildServiceProvider(), () => Disposed++);
        }

        private sealed class Scope(IServiceProvider provider, Action onDispose) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;

            public void Dispose() => onDispose();
        }
    }
}
