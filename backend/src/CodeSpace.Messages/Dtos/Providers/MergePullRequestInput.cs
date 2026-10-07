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
/// <c>MergePullRequest { MergeMethod, CommitTitle, CommitMessage, Sha }</c> + a follow-up branch delete, and
/// GitLab's <c>MergeRequestMerge { Squash, ShouldRemoveSourceBranch, Sha, … }</c>.
/// </summary>
public sealed record MergePullRequestInput
{
    /// <summary>How to integrate the commits. Default <see cref="PullRequestMergeMethod.Merge"/>.</summary>
    public PullRequestMergeMethod Method { get; init; } = PullRequestMergeMethod.Merge;

    /// <summary>Optional merge-commit title (squash/merge). Provider default when null.</summary>
    public string? CommitTitle { get; init; }

    /// <summary>Optional merge-commit message body. Provider default when null.</summary>
    public string? CommitMessage { get; init; }

    /// <summary>Delete the source branch after a successful merge — only from the pull request's own repository, never a same-named branch of the base for a fork's pull request. Default false.</summary>
    public bool DeleteSourceBranch { get; init; }

    /// <summary>Merge only while the head is still this commit (GitHub merge <c>sha</c>; GitLab accept <c>sha</c>): a head that moved fails the merge instead of merging commits nobody reviewed. Null merges whatever the head is.</summary>
    public string? ExpectedHeadSha { get; init; }

    /// <summary>Merge only while the pull request still targets this branch: one retargeted since fails the merge instead of landing on a branch nobody approved. Neither provider takes it as a precondition, so it is checked by reading the pull request just before the merge. Null merges into whatever the base is.</summary>
    public string? ExpectedBaseBranch { get; init; }
}

/// <summary>What became of a merged pull request's source branch. Provider-neutral.</summary>
public enum SourceBranchDeletion
{
    /// <summary>No delete was attempted: the merge did not ask for one, or nothing merged.</summary>
    NotRequested,

    /// <summary>The branch is gone from the pull request's own repository — this merge deleted it, or it was already gone when asked.</summary>
    Deleted,

    /// <summary>Handed to the provider, which removes the branch from the request's own source project after the merge when the merging identity may (GitLab).</summary>
    Requested,

    /// <summary>Kept: the head branch lives in another repository (a fork). The base repository's ref of the same name is a different branch, so nothing is deleted.</summary>
    SkippedFork,

    /// <summary>The delete was refused, could not be made, or was cancelled before it was confirmed. The merge still stands; the detail says why.</summary>
    Failed
}

/// <summary>Outcome of a merge: whether it merged, and (when available) the resulting commit sha + a provider message.</summary>
public sealed record RemotePullRequestMergeResult
{
    public required bool Merged { get; init; }
    public string? Sha { get; init; }
    public string? Message { get; init; }

    /// <summary>What became of the source branch. <see cref="SourceBranchDeletion.NotRequested"/> unless the merge asked for it to go.</summary>
    public SourceBranchDeletion SourceBranchDeletion { get; init; }

    /// <summary>The same in words: which branch, where, and why it was kept or not deleted. Null when no delete was asked for.</summary>
    public string? SourceBranchDetail { get; init; }
}
