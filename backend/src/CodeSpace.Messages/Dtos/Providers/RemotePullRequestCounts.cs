namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Total PR/MR counts per state for a repository. Used by the UI to render real
/// "Open N · Closed M" tab chips + true total-page pagination — without needing
/// to fetch every page just to know how many there are.
///
/// "Open" includes drafts (drafts are open with a flag on GitHub; "opened" with
/// draft=true on GitLab). "Closed" includes merged (merged is a sub-state of
/// closed on GitHub; we sum closed+merged on GitLab to match).
/// </summary>
public sealed record RemotePullRequestCounts
{
    public required int Open { get; init; }
    public required int Closed { get; init; }
}
