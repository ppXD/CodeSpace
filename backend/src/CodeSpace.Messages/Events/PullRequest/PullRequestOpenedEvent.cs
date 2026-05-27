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

    /// <summary>
    /// Label names attached to the PR at the moment the webhook fired. Provider source:
    /// GitHub <c>pull_request.labels[].name</c>; GitLab top-level <c>labels[].title</c>.
    /// Empty when the PR has no labels OR the provider didn't include the field on this
    /// event variant. Matchers reference this for label-scoped trigger configs; names are
    /// case-sensitive per both providers' conventions.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}
