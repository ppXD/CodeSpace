namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// One activity-timeline entry on an issue — the "assigned / labeled / milestoned / closed /
/// mentioned in MR" rows beneath the conversation. Provider-neutral: GitHub's structured issue
/// events and GitLab's system notes both normalise here. <see cref="Summary"/> is the pre-rendered
/// human text (GitLab gives it verbatim; GitHub we synthesise from the event type) so the UI just
/// shows an icon + the line. <see cref="Kind"/> drives the icon choice.
/// </summary>
public sealed record RemoteIssueEvent
{
    /// <summary>Stable provider id for the event/note — React key.</summary>
    public required string ExternalId { get; init; }

    /// <summary>
    /// Normalised event kind for icon selection: <c>assigned</c>, <c>unassigned</c>, <c>labeled</c>,
    /// <c>unlabeled</c>, <c>milestoned</c>, <c>demilestoned</c>, <c>closed</c>, <c>reopened</c>,
    /// <c>renamed</c>, <c>referenced</c>, <c>mentioned</c>, or <c>other</c> for anything unmapped.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>The human-readable line, e.g. "changed milestone to 1.1.1" or "mentioned in merge request !84".</summary>
    public required string Summary { get; init; }

    public string? ActorLogin { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
}
