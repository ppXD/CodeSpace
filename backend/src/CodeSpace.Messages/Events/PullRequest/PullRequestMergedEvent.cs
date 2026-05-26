namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestMergedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string MergedByExternalId { get; init; }
    public required string MergedByName { get; init; }
    public string? MergeCommitSha { get; init; }
}
