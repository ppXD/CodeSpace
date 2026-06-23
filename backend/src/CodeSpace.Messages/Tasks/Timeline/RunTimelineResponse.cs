namespace CodeSpace.Messages.Tasks.Timeline;

/// <summary>
/// The Activity-timeline payload for one run: the run's overall status (its <c>WorkflowRunStatus</c> NAME — open on
/// the wire) plus the merged, OccurredAt-sorted narrative events the <c>IRunTimelineProjector</c> fanned out across
/// every source. Read-only + poll-friendly (a GET; live push is a later concern).
/// </summary>
public sealed record RunTimelineResponse
{
    public required Guid RunId { get; init; }

    /// <summary>The run's overall status as its <c>WorkflowRunStatus</c> enum NAME (open string — never switched on).</summary>
    public required string RunStatus { get; init; }

    public required IReadOnlyList<RunTimelineEvent> Events { get; init; }
}
