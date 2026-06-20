using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Issues;

public sealed class IssueService : IIssueService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;
    private readonly IActorCredentialProvider _actorCredentials;

    public IssueService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker, IActorCredentialProvider actorCredentials)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
        _actorCredentials = actorCredentials;
    }

    public async Task<IReadOnlyList<RemoteIssue>> ListAsync(Guid repositoryId, Guid teamId, IssueState? state, int page, int perPage, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveCatalogAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        return await catalog.ListIssuesAsync(context, remote, state, page, perPage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteIssueCounts> GetCountsAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveCatalogAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        return await catalog.CountIssuesAsync(context, remote, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared READ preflight (mirrors <c>PullRequestService.ResolveAsync</c>): repo lookup → credential
    /// null-check → catalog scope check → capability + provider-context + remote-repository shape. Reads use
    /// the repo's connection credential — no Model-B actor attribution (listing isn't a per-user write).
    /// </summary>
    private async Task<(IIssueCatalogCapability Catalog, ProviderContext Context, RemoteRepository Remote)> ResolveCatalogAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IIssueCatalogCapability));

        var catalog = _registry.Require<IIssueCatalogCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential!);
        var remote = repo.ToRemoteRepository();

        return (catalog, context, remote);
    }

    public async Task<RemoteIssue> CreateAsync(Guid repositoryId, Guid teamId, CreateIssueInput input, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("An issue requires a title.");

        var repo = await LoadRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);

        // Per-user attribution (Model B), same as PR open: create AS the actor's own linked identity when
        // wired, else the repo's connection credential. Scope + role are checked against whichever acts.
        var credential = await ResolveActingCredentialAsync(repo, actorUserId, cancellationToken).ConfigureAwait(false);
        _scopeChecker.EnsureCapability(credential, repo.ProviderInstance.Provider, typeof(IIssueWriteCapability));

        var writeCap = _registry.Require<IIssueWriteCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, credential);
        var remote = repo.ToRemoteRepository();

        return await writeCap.CreateIssueAsync(context, remote, input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteIssueComment> CommentAsync(Guid repositoryId, Guid teamId, int number, string body, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidOperationException("A comment requires a body.");

        var repo = await LoadRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);

        var credential = await ResolveActingCredentialAsync(repo, actorUserId, cancellationToken).ConfigureAwait(false);
        _scopeChecker.EnsureCapability(credential, repo.ProviderInstance.Provider, typeof(IIssueWriteCapability));

        var writeCap = _registry.Require<IIssueWriteCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, credential);
        var remote = repo.ToRemoteRepository();

        return await writeCap.CommentIssueAsync(context, remote, number, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteIssue> CloseAsync(Guid repositoryId, Guid teamId, int number, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);

        var credential = await ResolveActingCredentialAsync(repo, actorUserId, cancellationToken).ConfigureAwait(false);
        _scopeChecker.EnsureCapability(credential, repo.ProviderInstance.Provider, typeof(IIssueWriteCapability));

        var writeCap = _registry.Require<IIssueWriteCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, credential);
        var remote = repo.ToRemoteRepository();

        return await writeCap.CloseIssueAsync(context, remote, number, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Actor's own credential when <paramref name="actorUserId"/> is set (throws
    /// ActorIdentityRequiredException if they haven't linked one); otherwise the repo's connection credential.</summary>
    private async Task<Credential> ResolveActingCredentialAsync(Repository repo, Guid? actorUserId, CancellationToken cancellationToken) =>
        actorUserId is { } uid
            ? await _actorCredentials.RequireAsync(uid, repo.ProviderInstance, cancellationToken).ConfigureAwait(false)
            : repo.Credential!;

    // Fail-closed tenant scope (mirrors PullRequestService / RunCommandService): the repo resolves ONLY within
    // the run's team, so a model-supplied / untrusted repositoryId can never reach another tenant's issues. A
    // repo in another team falls out of the filter → the same non-leaking "not found".
    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.TeamId == teamId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

    private static void EnsureCredentialBound(Repository repo)
    {
        if (repo.Credential == null)
            throw new InvalidOperationException($"Repository {repo.Id} has no bound credential — relink first.");
    }
}
