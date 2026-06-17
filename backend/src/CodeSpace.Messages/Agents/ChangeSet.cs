namespace CodeSpace.Messages.Agents;

/// <summary>
/// One repository's pull-request request within a multi-repo Change Set (multi-repo PR4, Rule 18.1 noun): which repo,
/// and the branches to open the PR between. The per-repo branches come from a multi-repo run's
/// <see cref="RepositoryRunResult"/> (its <see cref="RepositoryRunResult.ProducedBranch"/> is the source).
/// </summary>
public sealed record ChangeSetPullRequest
{
    public required Guid RepositoryId { get; init; }

    /// <summary>The head branch with the changes (this repo's produced branch). Empty/whitespace ⇒ the repo had no changes ⇒ the open is SKIPPED, not failed.</summary>
    public required string SourceBranch { get; init; }

    /// <summary>The base branch to open the PR into (this repo's own default / base ref).</summary>
    public required string TargetBranch { get; init; }
}

/// <summary>The authored intent for opening a multi-repo Change Set's pull requests: the per-repo branch pairs + a shared title/body/draft applied to each PR.</summary>
public sealed record ChangeSetPullRequestSpec
{
    public required IReadOnlyList<ChangeSetPullRequest> Repositories { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public bool Draft { get; init; }
}

/// <summary>How one repo's PR-open turned out within a Change Set.</summary>
public enum ChangeSetPullRequestDisposition
{
    /// <summary>A PR was opened (its number/url/state are populated).</summary>
    Opened,

    /// <summary>The repo had no source branch (no changes) — nothing to open, not a failure.</summary>
    Skipped,

    /// <summary>The provider rejected the open (scope / permission / validation) — the redacted reason is in <c>Error</c>; the rest of the set is unaffected.</summary>
    Failed,
}

/// <summary>One repository's PR-open outcome within a Change Set — the honesty invariant: one repo's failure is recorded here, never sinking the whole set.</summary>
public sealed record ChangeSetPullRequestOutcome
{
    public required Guid RepositoryId { get; init; }

    public required ChangeSetPullRequestDisposition Disposition { get; init; }

    public int? Number { get; init; }

    public string? Url { get; init; }

    public string? State { get; init; }

    /// <summary>The skip reason (Skipped) or the redacted provider failure (Failed); null when Opened.</summary>
    public string? Error { get; init; }
}

/// <summary>The result of opening a multi-repo Change Set's pull requests: one outcome per requested repo + roll-up counts (the workflow branches on <see cref="FailedCount"/>).</summary>
public sealed record ChangeSetResult
{
    public required IReadOnlyList<ChangeSetPullRequestOutcome> PullRequests { get; init; }

    public int OpenedCount { get; init; }

    public int SkippedCount { get; init; }

    public int FailedCount { get; init; }
}
