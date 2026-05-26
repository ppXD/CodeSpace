namespace CodeSpace.Messages.Events.Issue;

public sealed class IssueClosedEvent : NormalizedEvent
{
    public required string ExternalIssueId { get; init; }
    public required int Number { get; init; }
    public required string ClosedByExternalId { get; init; }
    public required string ClosedByName { get; init; }
}
