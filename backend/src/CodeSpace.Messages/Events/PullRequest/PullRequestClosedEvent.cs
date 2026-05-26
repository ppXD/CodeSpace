namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestClosedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string ClosedByExternalId { get; init; }
    public required string ClosedByName { get; init; }
}
