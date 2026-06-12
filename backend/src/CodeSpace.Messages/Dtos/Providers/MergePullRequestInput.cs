namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>How a pull/merge request's commits are integrated when merged. Provider-neutral.</summary>
public enum PullRequestMergeMethod
{
    /// <summary>A merge commit joining the branch (GitHub <c>merge</c>, GitLab default).</summary>
    Merge,

    /// <summary>Squash all commits into one (GitHub <c>squash</c>, GitLab <c>squash: true</c>).</summary>
    Squash,

    /// <summary>Rebase the commits onto the target (GitHub <c>rebase</c>, GitLab rebase-merge).</summary>
    Rebase
}

/// <summary>
/// Provider-neutral request to MERGE an open pull/merge request. Maps onto GitHub's
/// <c>MergePullRequest { MergeMethod, CommitTitle, CommitMessage }</c> + a follow-up branch delete, and
/// GitLab's <c>MergeRequestMerge { Squash, ShouldRemoveSourceBranch, … }</c>.
/// </summary>
public sealed record MergePullRequestInput
{
    /// <summary>How to integrate the commits. Default <see cref="PullRequestMergeMethod.Merge"/>.</summary>
    public PullRequestMergeMethod Method { get; init; } = PullRequestMergeMethod.Merge;

    /// <summary>Optional merge-commit title (squash/merge). Provider default when null.</summary>
    public string? CommitTitle { get; init; }

    /// <summary>Optional merge-commit message body. Provider default when null.</summary>
    public string? CommitMessage { get; init; }

    /// <summary>Delete the source branch after a successful merge. Default false.</summary>
    public bool DeleteSourceBranch { get; init; }
}

/// <summary>Outcome of a merge: whether it merged, and (when available) the resulting commit sha + a provider message.</summary>
public sealed record RemotePullRequestMergeResult
{
    public required bool Merged { get; init; }
    public string? Sha { get; init; }
    public string? Message { get; init; }
}
