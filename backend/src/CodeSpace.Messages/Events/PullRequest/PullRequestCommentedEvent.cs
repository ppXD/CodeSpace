namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestCommentedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string CommentExternalId { get; init; }
    public required string CommenterExternalId { get; init; }
    public required string CommenterName { get; init; }
    public required string Body { get; init; }
    public string? FilePath { get; init; }
    public int? LineNumber { get; init; }
}
