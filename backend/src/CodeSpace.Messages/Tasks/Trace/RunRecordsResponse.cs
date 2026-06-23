namespace CodeSpace.Messages.Tasks.Trace;

/// <summary>
/// The Trace-tab payload for one run: the run's overall status (its <c>WorkflowRunStatus</c> NAME — open on the wire,
/// matching <c>RunTimelineResponse</c>) plus EVERY <see cref="RunRecordView"/> in the run's append-only ledger, in
/// Sequence order. The raw audit beside the narrative timeline. Read-only + poll-friendly (a GET).
/// </summary>
public sealed record RunRecordsResponse
{
    public required Guid RunId { get; init; }

    /// <summary>The run's overall status as its <c>WorkflowRunStatus</c> enum NAME (open string — never switched on).</summary>
    public required string RunStatus { get; init; }

    public required IReadOnlyList<RunRecordView> Records { get; init; }
}
