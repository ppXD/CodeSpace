namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestOpenedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public required string SourceBranch { get; init; }
    public required string TargetBranch { get; init; }
    public required string AuthorExternalId { get; init; }
    public required string AuthorName { get; init; }
    public required string WebUrl { get; init; }
}
