using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Repositories;

public sealed class RepositoryHistoryService : IRepositoryHistoryService, IScopedDependency
{
    /// <summary>The file list's last-commit column costs one provider call per row. Cap how many we'll enrich in one request so a huge folder can't fan out into hundreds of calls.</summary>
    public const int MaxTreeCommitPaths = 100;

    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;

    public RepositoryHistoryService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
    }

    public async Task<RemoteCommitSummary?> GetLatestCommitAsync(Guid repositoryId, string? path, string? reference, CancellationToken cancellationToken)
    {
        var (history, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await history.GetLatestCommitAsync(context, remote, path, reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, RemoteCommitSummary>> GetTreeCommitsAsync(Guid repositoryId, IReadOnlyList<string> paths, string? reference, CancellationToken cancellationToken)
    {
        var capped = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().Take(MaxTreeCommitPaths).ToList();
        if (capped.Count == 0) return new Dictionary<string, RemoteCommitSummary>();

        var (history, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await history.ListPathCommitsAsync(context, remote, capped, reference, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IRepositoryHistoryCapability History, ProviderContext Context, RemoteRepository Remote)> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IRepositoryHistoryCapability));

        var history = _registry.Require<IRepositoryHistoryCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential!);
        var remote = ToRemoteRepository(repo);

        return (history, context, remote);
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken) =>
        await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

    private static void EnsureCredentialBound(Repository repo)
    {
        if (repo.Credential == null)
            throw new InvalidOperationException($"Repository {repo.Id} has no bound credential — relink first.");
    }

    private static RemoteRepository ToRemoteRepository(Repository repo) => new()
    {
        ExternalId = repo.ExternalId,
        NamespacePath = repo.NamespacePath,
        Name = repo.Name,
        FullPath = repo.FullPath,
        DefaultBranch = repo.DefaultBranch,
        Visibility = repo.Visibility,
        Description = repo.Description,
        WebUrl = repo.WebUrl,
        CloneUrlHttps = repo.CloneUrlHttps,
        CloneUrlSsh = repo.CloneUrlSsh,
        Archived = repo.Archived
    };
}
