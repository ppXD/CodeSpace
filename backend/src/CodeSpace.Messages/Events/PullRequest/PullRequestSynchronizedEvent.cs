namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestSynchronizedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string PreviousHeadSha { get; init; }
    public required string NewHeadSha { get; init; }
}
