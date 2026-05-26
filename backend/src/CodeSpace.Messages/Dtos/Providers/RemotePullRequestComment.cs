namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Provider-neutral PR comment shape returned from a post operation.
/// </summary>
public sealed record RemotePullRequestComment
{
    public required string ExternalId { get; init; }
    public required string Body { get; init; }
    public required string AuthorName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? WebUrl { get; init; }
}
