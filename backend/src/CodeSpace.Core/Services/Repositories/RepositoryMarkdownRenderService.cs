using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Repositories;

public sealed class RepositoryMarkdownRenderService : IRepositoryMarkdownRenderService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;

    public RepositoryMarkdownRenderService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
    }

    public async Task<RemoteRenderedMarkdown> RenderMarkdownAsync(Guid repositoryId, string markdown, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(markdown)) return new RemoteRenderedMarkdown { Html = string.Empty };

        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IRepositoryMarkdownRenderCapability));

        // Require throws NotSupportedException when the provider can't render markdown (generic Git) —
        // the SPA catches it and renders the markdown client-side instead.
        var render = _registry.Require<IRepositoryMarkdownRenderCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential!);

        return await render.RenderMarkdownAsync(context, ToRemoteRepository(repo), markdown, cancellationToken).ConfigureAwait(false);
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
