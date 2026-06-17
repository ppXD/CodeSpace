using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Resolves the agent workspace from the task's bound <c>RepositoryId</c> — the first (and common)
/// workspace source. Loads the repository (team-scoped) with its provider instance + credential, then
/// produces a <see cref="WorkspaceRequest"/>: the HTTPS clone URL, the default branch, and a short-lived
/// access token resolved through the same <see cref="IProviderAuthResolver"/> the providers use (so OAuth
/// refresh + every auth type are handled in one place). A run with no <c>RepositoryId</c> needs no
/// workspace and resolves to <c>null</c>.
/// </summary>
public sealed class RepositoryWorkspaceResolver : IAgentWorkspaceResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderAuthResolver _auth;

    public RepositoryWorkspaceResolver(CodeSpaceDbContext db, IProviderAuthResolver auth)
    {
        _db = db;
        _auth = auth;
    }

    public Task<WorkspaceRequest?> ResolveAsync(AgentTask task, Guid teamId, CancellationToken cancellationToken) =>
        task.RepositoryId is { } repositoryId
            ? ResolveByRepositoryIdAsync(repositoryId, teamId, cancellationToken)
            : Task.FromResult<WorkspaceRequest?>(null);

    public async Task<WorkspaceRequest?> ResolveByRepositoryIdAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(repo.CloneUrlHttps))
            throw new WorkspaceException($"Repository {repositoryId} has no HTTPS clone URL to clone from.");

        var token = await ResolveTokenAsync(repo, cancellationToken).ConfigureAwait(false);

        return new WorkspaceRequest
        {
            RepositoryUrl = repo.CloneUrlHttps,
            Ref = repo.DefaultBranch,
            Token = token,
            TokenUsername = token is null ? null : TokenUsernameFor(repo.ProviderInstance.Provider),
        };
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false)
        ?? throw new WorkspaceException($"Repository {repositoryId} not found for this team.");

    /// <summary>Resolve a clone token through the provider auth layer. A repo with no bound credential clones anonymously (public repo).</summary>
    private async Task<string?> ResolveTokenAsync(Repository repo, CancellationToken cancellationToken)
    {
        if (repo.Credential is null) return null;

        var auth = await _auth.ResolveAsync(new ProviderContext(repo.ProviderInstance, repo.Credential), cancellationToken).ConfigureAwait(false);

        return auth.Token;
    }

    /// <summary>The HTTPS basic-auth username each provider expects paired with a token. Pure + internal so it's unit-pinned (Rule 8 spirit).</summary>
    internal static string TokenUsernameFor(ProviderKind provider) => provider switch
    {
        ProviderKind.GitHub => "x-access-token",
        ProviderKind.GitLab => "oauth2",
        _ => "x-access-token",
    };
}
