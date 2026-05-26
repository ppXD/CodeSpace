namespace CodeSpace.Messages.Events.Issue;

public sealed class IssueOpenedEvent : NormalizedEvent
{
    public required string ExternalIssueId { get; init; }
    public required int Number { get; init; }
    public required string Title { get; init; }
    public string? Body { get; init; }
    public required string AuthorExternalId { get; init; }
    public required string AuthorName { get; init; }
    public required string WebUrl { get; init; }
}
