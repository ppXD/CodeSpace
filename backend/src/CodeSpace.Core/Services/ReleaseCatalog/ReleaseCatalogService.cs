using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.ReleaseCatalog;

public sealed class ReleaseCatalogService : IReleaseCatalogService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;

    public ReleaseCatalogService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
    }

    public async Task<IReadOnlyList<RemoteRelease>> ListReleasesAsync(Guid repositoryId, int page, int perPage, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.ListReleasesAsync(context, remote, page, perPage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteTag>> ListTagsAsync(Guid repositoryId, int page, int perPage, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.ListTagsAsync(context, remote, page, perPage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Same preflight as RepositoryInsightsService — repo lookup → credential null-check → source-read scope → capability.</summary>
    private async Task<(IReleaseCatalogCapability Catalog, ProviderContext Context, RemoteRepository Remote)> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IReleaseCatalogCapability));

        var catalog = _registry.Require<IReleaseCatalogCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential!);
        var remote = repo.ToRemoteRepository();

        return (catalog, context, remote);
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
}
