namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral git tag — the Releases page's Tags tab. GitHub <c>RepositoryTag</c> and GitLab
/// <c>Tag</c> both normalise here. <see cref="CommitSha"/> is the tagged commit; <see cref="Message"/>
/// is the annotated-tag message (null for lightweight tags).
/// </summary>
public sealed record RemoteTag
{
    public required string Name { get; init; }
    public required string CommitSha { get; init; }
    public string? Message { get; init; }
    public string? WebUrl { get; init; }
}
