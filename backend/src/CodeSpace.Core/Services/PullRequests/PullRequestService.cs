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

namespace CodeSpace.Core.Services.PullRequests;

public sealed class PullRequestService : IPullRequestService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IScopeChecker _scopeChecker;
    private readonly IActorCredentialProvider _actorCredentials;

    public PullRequestService(CodeSpaceDbContext db, IProviderRegistry registry, IScopeChecker scopeChecker, IActorCredentialProvider actorCredentials)
    {
        _db = db;
        _registry = registry;
        _scopeChecker = scopeChecker;
        _actorCredentials = actorCredentials;
    }

    public async Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid repositoryId, PullRequestState? state, int page, int perPage, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.ListPullRequestsAsync(context, remote, state, page, perPage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequest> GetAsync(Guid repositoryId, int number, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.GetPullRequestAsync(context, remote, number, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid repositoryId, int number, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.ListPullRequestCommitsAsync(context, remote, number, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid repositoryId, int number, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.ListPullRequestFilesAsync(context, remote, number, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestCounts> GetCountsAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.CountPullRequestsAsync(context, remote, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid repositoryId, int number, CancellationToken cancellationToken)
    {
        var (catalog, context, remote) = await ResolveAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        return await catalog.ListChecksAsync(context, remote, number, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestComment> PostCommentAsync(Guid repositoryId, int number, string body, CancellationToken cancellationToken)
    {
        // Comment WRITE preflight uses the same repo/credential lookup but enforces the
        // narrower IPullRequestCommentCapability scope (e.g. GitLab requires `api`, not just `read_api`).
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IPullRequestCommentCapability));

        var commentCap = _registry.Require<IPullRequestCommentCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, repo.Credential!);
        var remote = repo.ToRemoteRepository();

        return await commentCap.PostCommentAsync(context, remote, number, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequestReview> SubmitReviewAsync(Guid repositoryId, int number, PullRequestReviewVerdict verdict, string? body, Guid? actorUserId, CancellationToken cancellationToken)
    {
        // A comment / request-changes verdict needs something to say; approve may stand alone (LGTM).
        if (verdict != PullRequestReviewVerdict.Approve && string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException($"A '{verdict}' review requires a non-empty body.");

        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);

        // Per-user attribution (Model B): act AS the actor's own linked identity when one is wired,
        // else fall back to the repo's connection credential (unchanged behaviour). Scope is checked
        // against whichever credential actually makes the call.
        var credential = await ResolveActingCredentialAsync(repo, actorUserId, cancellationToken).ConfigureAwait(false);
        _scopeChecker.EnsureCapability(credential, repo.ProviderInstance.Provider, typeof(IPullRequestReviewCapability));

        var reviewCap = _registry.Require<IPullRequestReviewCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, credential);
        var remote = repo.ToRemoteRepository();

        return await reviewCap.SubmitReviewAsync(context, remote, number, verdict, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemotePullRequest> OpenPullRequestAsync(Guid repositoryId, OpenPullRequestInput input, Guid? actorUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) throw new InvalidOperationException("A pull request requires a title.");
        if (string.IsNullOrWhiteSpace(input.SourceBranch) || string.IsNullOrWhiteSpace(input.TargetBranch))
            throw new InvalidOperationException("A pull request requires both a source and a target branch.");
        if (string.Equals(input.SourceBranch, input.TargetBranch, StringComparison.Ordinal))
            throw new InvalidOperationException("The source and target branch must differ.");

        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);

        // Per-user attribution (Model B), same as SubmitReviewAsync: open AS the actor's own linked identity
        // when wired, else the repo's connection credential. Scope + role are checked against whichever acts.
        var credential = await ResolveActingCredentialAsync(repo, actorUserId, cancellationToken).ConfigureAwait(false);
        _scopeChecker.EnsureCapability(credential, repo.ProviderInstance.Provider, typeof(IPullRequestWriteCapability));

        var writeCap = _registry.Require<IPullRequestWriteCapability>(repo.ProviderInstance.Provider);
        var context = new ProviderContext(repo.ProviderInstance, credential);
        var remote = repo.ToRemoteRepository();

        return await writeCap.OpenPullRequestAsync(context, remote, input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Actor's own credential when <paramref name="actorUserId"/> is set (throws
    /// ActorIdentityRequiredException if they haven't linked one); otherwise the repo's connection credential.</summary>
    private async Task<Credential> ResolveActingCredentialAsync(Repository repo, Guid? actorUserId, CancellationToken cancellationToken) =>
        actorUserId is { } uid
            ? await _actorCredentials.RequireAsync(uid, repo.ProviderInstance, cancellationToken).ConfigureAwait(false)
            : repo.Credential!;

    /// <summary>
    /// Shared preflight for every PR call: repo lookup → credential null-check →
    /// scope check → capability + provider-context + remote-repository shape.
    /// Throws InvalidOperationException (mapped to 400) or
    /// ProviderInsufficientScopeException (422) on failure.
    /// </summary>
    private async Task<(IPullRequestCatalogCapability Catalog, ProviderContext Context, RemoteRepository Remote)> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        EnsureCredentialBound(repo);
        EnsureScopeCovered(repo);

        var catalog = _registry.Require<IPullRequestCatalogCapability>(repo.ProviderInstance.Provider);
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

    private void EnsureScopeCovered(Repository repo) =>
        _scopeChecker.EnsureCapability(repo.Credential!, repo.ProviderInstance.Provider, typeof(IPullRequestCatalogCapability));
}
