namespace CodeSpace.Messages.Enums;

/// <summary>
/// Provider-neutral pull/merge request lifecycle state. Maps from GitHub's
/// (open, closed, merged) and GitLab's (opened, closed, merged, locked).
/// "Draft" is orthogonal to state on GitHub (it's a flag on an Open PR) and
/// distinct on GitLab (`work_in_progress` / `draft: true`) — represented here
/// as a sibling state so the UI can render the dotted-grey draft icon directly
/// without needing both <see cref="IsDraft"/> + <see cref="State"/>.
/// </summary>
public enum PullRequestState
{
    Open,
    Draft,
    Merged,
    Closed
}
