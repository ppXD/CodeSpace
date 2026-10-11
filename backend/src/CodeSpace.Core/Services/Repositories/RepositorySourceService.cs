using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Repositories;

public sealed class RepositorySourceService : IRepositorySourceService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;

    public RepositorySourceService(CodeSpaceDbContext db, ICurrentTeam currentTeam, IProviderRegistry registry, IScopeChecker scopeChecker)
    {
        _db = db;
        _currentTeam = currentTeam;
        _registry = registry;
        _scopeChecker = scopeChecker;
    }

    public async Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var (source, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await source.ListBranchesAsync(context, remote, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(Guid repositoryId, string? path, string? reference, CancellationToken cancellationToken)
    {
        var (source, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await source.ListTreeAsync(context, remote, path, reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteFileContent> GetFileAsync(Guid repositoryId, string path, string? reference, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("A file path is required.");

        var (source, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await source.GetFileAsync(context, remote, path, reference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared preflight for every source call: repo lookup → credential null-check → source-read scope
    /// check → capability + provider-context + remote-repository shape. Mirrors PullRequestService.ResolveAsync.
    /// </summary>
    private async Task<(IRepositorySourceCapability Source, ProviderContext Context, RemoteRepository Remote)> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IRepositorySourceCapability));

        var source = _registry.Require<IRepositorySourceCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential!);
        var remote = repo.ToRemoteRepository();

        return (source, context, remote);
    }

    /// <summary>
    /// Team-scoped as well as id-scoped: the repository's OWN credential is what gets spent, so a repository outside the
    /// caller's team must read as absent even when a request reaches here without <c>IRequireRepositoryAccess</c> — that
    /// gap is how agent-pack preview read another team's private files. No team selected matches nothing.
    /// </summary>
    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken) =>
        await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == _currentTeam.Id && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

    private static void EnsureCredentialBound(Repository repo)
    {
        if (repo.Credential == null)
            throw new InvalidOperationException($"Repository {repo.Id} has no bound credential — relink first.");
    }
}
