using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Repositories;

public sealed class RepositoryInsightsService : IRepositoryInsightsService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;

    public RepositoryInsightsService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
    }

    public async Task<RemoteRepositoryStats> GetStatsAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var (insights, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await insights.GetStatsAsync(context, remote, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteLanguage>> GetLanguagesAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var (insights, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await insights.GetLanguagesAsync(context, remote, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteRelease?> GetLatestReleaseAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var (insights, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await insights.GetLatestReleaseAsync(context, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Same preflight as RepositorySourceService — repo lookup → credential null-check → source-read scope → capability.</summary>
    private async Task<(IRepositoryInsightsCapability Insights, ProviderContext Context, RemoteRepository Remote)> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IRepositoryInsightsCapability));

        var insights = _registry.Require<IRepositoryInsightsCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential!);
        var remote = repo.ToRemoteRepository();

        return (insights, context, remote);
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
