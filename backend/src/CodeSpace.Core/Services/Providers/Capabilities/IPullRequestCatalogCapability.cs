using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Listing pull/merge requests for a repository. Live read against the provider —
/// we deliberately do NOT cache PRs locally yet. State filter is optional; null
/// means "all states". Pagination is intentionally absent for v0 — return up to
/// <see cref="ListPullRequestsAsync"/>'s implementation cap (typically the first
/// page, ordered by updated-desc) and add cursoring when the UI grows past it.
/// </summary>
public interface IPullRequestCatalogCapability : IProviderCapability
{
    /// <summary>
    /// List PRs/MRs filtered by state, one page at a time. <paramref name="page"/> is
    /// 1-based; <paramref name="perPage"/> is the page size (capped by the provider —
    /// GitHub allows 100, GitLab allows 100). Callers infer "there's more" from
    /// <c>result.Count == perPage</c>.
    /// </summary>
    Task<IReadOnlyList<RemotePullRequest>> ListPullRequestsAsync(ProviderContext context, RemoteRepository repository, PullRequestState? stateFilter, int page, int perPage, CancellationToken cancellationToken);

    /// <summary>
    /// DC-2c — the OPEN PR/MR whose source (head) branch is <paramref name="sourceBranch"/>, or null when none
    /// exists. Uses the provider's OWN native branch filter (GitHub's <c>PullRequestRequest.Head</c>, GitLab's
    /// <c>MergeRequestQuery.SourceBranch</c>) rather than paging through <see cref="ListPullRequestsAsync"/> and
    /// filtering client-side — a repo with many open PRs would otherwise need an unbounded scan for the ONE we
    /// actually care about. <see cref="IPullRequestWriteCapability.OpenPullRequestAsync"/>'s own bind-or-create
    /// idempotency check calls this as its fallback on a duplicate-branch create failure — the caller ALSO
    /// verifies the found PR's target branch matches the create request's before binding to it, since this
    /// method itself filters only on the source branch.
    /// </summary>
    Task<RemotePullRequest?> FindPullRequestByBranchAsync(ProviderContext context, RemoteRepository repository, string sourceBranch, CancellationToken cancellationToken);

    /// <summary>
    /// Fetch a single PR with its full body + diff stats. <paramref name="number"/> is the
    /// per-repo number shown to users (#42 on GitHub, !42 on GitLab) — NOT the global
    /// <see cref="RemotePullRequest.ExternalId"/>. Returns null when the PR doesn't exist
    /// (translated to 404 by GlobalExceptionFilter via the resilience layer).
    /// </summary>
    Task<RemotePullRequest> GetPullRequestAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken);

    /// <summary>Commits attached to the PR/MR, ordered oldest-first (matching what the SPA renders top-to-bottom in the Commits tab).</summary>
    Task<IReadOnlyList<RemotePullRequestCommit>> ListPullRequestCommitsAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Files changed in the PR/MR with their unified-diff patch. The provider may suppress
    /// the patch text for large/binary files (Patch == null) — the UI handles that.
    /// </summary>
    Task<IReadOnlyList<RemotePullRequestFile>> ListPullRequestFilesAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken);

    /// <summary>
    /// Total open + closed PR counts for a repository. Provider-side aggregation
    /// (GitHub Search API; GitLab GraphQL) — cheap enough to call separately from
    /// the page fetch, and the SPA caches it per repo so the user sees real
    /// "Open N · Closed M" tab counts without scrolling through every page.
    /// </summary>
    Task<RemotePullRequestCounts> CountPullRequestsAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken);

    /// <summary>
    /// CI / check-runs for the PR's HEAD commit. Empty list when the provider has no
    /// checks configured. Implementations should be resilient — a token without the
    /// right scope for Actions/Pipelines reads MUST return an empty list, not throw,
    /// because the PR detail view falls back to "no checks" gracefully.
    /// </summary>
    Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(ProviderContext context, RemoteRepository repository, int number, CancellationToken cancellationToken);
}
