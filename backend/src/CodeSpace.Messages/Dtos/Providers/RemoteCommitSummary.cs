namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One commit as the Code tab shows it — the latest-commit header bar and each file row's last-commit
/// column. <see cref="Message"/> is the first line only (the summary). <see cref="AuthorAvatarUrl"/> is
/// null on providers that don't expose it on a commit (GitLab).
/// </summary>
public sealed record RemoteCommitSummary
{
    public required string Sha { get; init; }
    public required string ShortSha { get; init; }
    public required string Message { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public DateTimeOffset? CommittedDate { get; init; }
    public string? WebUrl { get; init; }
}
