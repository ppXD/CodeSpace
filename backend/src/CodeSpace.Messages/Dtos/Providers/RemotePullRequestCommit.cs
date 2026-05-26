namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One commit attached to a PR/MR. Provider-neutral subset of GitHub's PullRequestCommit
/// and GitLab's Commit — only the fields the Commits-tab UI needs. The full SHA is preserved
/// so future code-search / inline-blame features can correlate against the repository.
/// </summary>
public sealed record RemotePullRequestCommit
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }

    /// <summary>First line of the commit message (the subject). Always present.</summary>
    public required string Title { get; init; }

    /// <summary>Rest of the commit message after the blank line. Null when the commit is a single-liner.</summary>
    public string? Body { get; init; }

    public string? AuthorLogin { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorEmail { get; init; }

    public required DateTimeOffset AuthoredDate { get; init; }

    /// <summary>Web URL to view the commit on the provider. Useful as an escape hatch in the UI.</summary>
    public string? WebUrl { get; init; }
}
