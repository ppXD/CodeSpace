namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One page of accessible repositories. Used by the Add-Repository flow:
///
///   - <see cref="Items"/> is the repos on this page (already filtered by search where
///     the provider supports a server-side `search` parameter — GitLab does; GitHub
///     does not and so the SPA must filter client-side after eager-fetching the full list).
///   - <see cref="TotalCount"/> is the provider-side total when cheaply available, null
///     otherwise; drives whether the SPA can render a last-page anchor or has to fall
///     back to an open-ended pager.
/// </summary>
public sealed record RemoteRepositoryPage
{
    public required IReadOnlyList<RemoteRepository> Items { get; init; }

    /// <summary>
    /// Provider-side total — same approach as RemotePullRequestCounts. Lets the SPA
    /// render true GitHub-style pagination with a last-page anchor + a "N repos" chip.
    /// Null when the provider doesn't give us a cheap total: GitHub's <c>/user/repos</c>
    /// has no count in the response body, and NGitLab doesn't expose GitLab's X-Total
    /// header through the typed client. The pager degrades to its open-ended shape.
    /// </summary>
    public int? TotalCount { get; init; }
}
