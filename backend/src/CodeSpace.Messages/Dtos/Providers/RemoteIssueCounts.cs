namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Total issue counts per state for a repository. Powers the "Open N · Closed M"
/// filter chips + true total-page pagination without fetching every page. Mirrors
/// <see cref="RemotePullRequestCounts"/> — both GitHub and GitLab issues are simply
/// open or closed (no merged sub-state to fold in, unlike PRs).
/// </summary>
public sealed record RemoteIssueCounts
{
    public required int Open { get; init; }
    public required int Closed { get; init; }
}
