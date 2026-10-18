using CodeSpace.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// <see cref="IWorkspaceTokenSource"/> over the same resolve a run's workspace was provisioned with: it asks the scoped
/// <see cref="IAgentWorkspaceResolver"/> for the repository again, so the repository is loaded team-scoped and non-deleted
/// with its CURRENT credential row, and the token comes through the provider auth layer — which refreshes an OAuth token
/// that is due, under the refresh lock, and reads a pair another run already rotated rather than refreshing it twice.
/// Nothing is cached here: the stored credential is the only truth, so there is nothing to invalidate after a refusal — asking
/// again IS the invalidation.
///
/// <para>A singleton (the workspace provider that asks is one) that opens one short DI scope per call, the way the
/// executor reaches its other scoped services: a workspace outlives any request scope, and a long-lived scope's DbContext
/// would serve the very credential row this exists to re-read.</para>
/// </summary>
public sealed class RepositoryWorkspaceTokenSource : IWorkspaceTokenSource, ISingletonDependency
{
    private readonly IServiceScopeFactory _scopes;

    public RepositoryWorkspaceTokenSource(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task<string?> ResolveAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();

        var request = await scope.ServiceProvider.GetRequiredService<IAgentWorkspaceResolver>().ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);

        return request?.Token;
    }
}
