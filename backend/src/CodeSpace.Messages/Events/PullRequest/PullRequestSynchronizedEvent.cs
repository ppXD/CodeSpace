namespace CodeSpace.Messages.Events.PullRequest;

public sealed class PullRequestSynchronizedEvent : NormalizedEvent
{
    public required string ExternalPullRequestId { get; init; }
    public required int Number { get; init; }
    public required string PreviousHeadSha { get; init; }
    public required string NewHeadSha { get; init; }

    /// <summary>
    /// Label names attached to the PR at the moment the webhook fired. Provider source:
    /// GitHub <c>pull_request.labels[].name</c>; GitLab top-level <c>labels[].title</c>.
    /// Empty when the PR has no labels OR the provider didn't include the field on this
    /// event variant. Matchers reference this for label-scoped trigger configs; names are
    /// case-sensitive per both providers' conventions.
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the PR is a draft / work-in-progress when the webhook fired. Provider source:
    /// GitHub <c>pull_request.draft</c>; GitLab <c>object_attributes.draft</c> (falling back to
    /// the legacy <c>work_in_progress</c>). Defaults to false when the provider omits the field.
    /// Surfaced as <c>{{trigger.isDraft}}</c> so a workflow can gate on it.
    /// </summary>
    public bool IsDraft { get; init; }
}
