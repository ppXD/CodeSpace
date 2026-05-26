namespace CodeSpace.Messages.Events.Push;

public sealed class PushReceivedEvent : NormalizedEvent
{
    public required string Ref { get; init; }
    public required string BeforeSha { get; init; }
    public required string AfterSha { get; init; }
    public required string PusherExternalId { get; init; }
    public required string PusherName { get; init; }
    public required IReadOnlyList<CommitSummary> Commits { get; init; }
}

public sealed record CommitSummary
{
    public required string Sha { get; init; }
    public required string Message { get; init; }
    public required string AuthorEmail { get; init; }
    public required string AuthorName { get; init; }
}
