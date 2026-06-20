using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Listing issues for a repository — the READ half of the issue surface, sibling to
/// <see cref="IIssueWriteCapability"/> and modelled exactly on
/// <see cref="IPullRequestCatalogCapability"/>. Live read against the provider; nothing is
/// cached locally. Rule 7 (ISP): a provider that can't list issues simply doesn't implement
/// this — the registry resolves it by type and the Issues tab degrades for that provider.
///
/// <para>A new provider implements just this interface — the registry resolves it by <c>ProviderKind</c>.</para>
/// </summary>
public interface IIssueCatalogCapability : IProviderCapability
{
    /// <summary>
    /// List issues filtered by state, one page at a time. <paramref name="page"/> is 1-based;
    /// <paramref name="perPage"/> is the page size (capped by the provider — 100 on both).
    /// Callers infer "there's more" from <c>result.Count == perPage</c>. Implementations MUST
    /// exclude pull/merge requests (GitHub's issues API returns PRs as issues — filter them out).
    /// </summary>
    Task<IReadOnlyList<RemoteIssue>> ListIssuesAsync(ProviderContext context, RemoteRepository repository, IssueState? stateFilter, int page, int perPage, CancellationToken cancellationToken);

    /// <summary>
    /// Total open + closed issue counts for a repository. Provider-side aggregation
    /// (GitHub Search API; GitLab GraphQL) — cheap enough to call separately from the page
    /// fetch, and the SPA caches it per repo so the filter chips show real counts without
    /// walking every page.
    /// </summary>
    Task<RemoteIssueCounts> CountIssuesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken);
}
