namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestMergedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string MergedByExternalId { get; init; }
    public required string MergedByName { get; init; }
    public string? MergeCommitSha { get; init; }

    /// <summary>
    /// Label names attached to the PR at the moment it merged. Provider source:
    /// GitHub <c>pull_request.labels[].name</c>; GitLab top-level <c>labels[].title</c>.
    /// Empty when the PR had no labels OR the provider didn't include the field. Lets the
    /// merged trigger reuse the same repository + label filter as <c>PullRequestOpenedEvent</c>
    /// (e.g. "run a deploy when a PR labelled <c>release</c> merges"); names are case-sensitive.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}
