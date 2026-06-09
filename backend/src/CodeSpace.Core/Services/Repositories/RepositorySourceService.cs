using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Repositories;

public sealed class RepositorySourceService : IRepositorySourceService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;

    public RepositorySourceService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker)
    {
        _db = db;
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
