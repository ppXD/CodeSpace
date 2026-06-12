namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral request to OPEN a pull/merge request between two existing branches on a repository.
/// Maps onto GitHub's <c>NewPullRequest(title, head, base)</c> and GitLab's
/// <c>MergeRequestCreate { SourceBranch, TargetBranch, Title, Description }</c>. The branches must already
/// exist on the remote — this opens the request, it does not push code.
/// </summary>
public sealed record OpenPullRequestInput
{
    /// <summary>The PR/MR title.</summary>
    public required string Title { get; init; }

    /// <summary>The branch with the changes (GitHub <c>head</c> / GitLab <c>source_branch</c>).</summary>
    public required string SourceBranch { get; init; }

    /// <summary>The branch to merge into (GitHub <c>base</c> / GitLab <c>target_branch</c>).</summary>
    public required string TargetBranch { get; init; }

    /// <summary>Optional markdown body / description.</summary>
    public string? Body { get; init; }

    /// <summary>Open as a draft / work-in-progress when the provider supports it. Default false.</summary>
    public bool Draft { get; init; }
}
