namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral issue-comment shape returned from a post operation. Mirrors
/// <see cref="RemotePullRequestComment"/>. <see cref="WebUrl"/> is nullable because GitLab issue
/// notes carry no web URL (GitHub issue comments do, via <c>HtmlUrl</c>).
/// </summary>
public sealed record RemoteIssueComment
{
    public required string ExternalId { get; init; }
    public required string Body { get; init; }
    public required string AuthorName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? WebUrl { get; init; }
}
