namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestApprovedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string ReviewerExternalId { get; init; }
    public required string ReviewerName { get; init; }
    public string? ReviewBody { get; init; }
}
