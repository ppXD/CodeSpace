namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// The background-tasks UI payload for one run: the run's overall status (its <c>WorkflowRunStatus</c> NAME — open
/// on the wire) plus the merged, Order-sorted phase tree the <c>IRunPhaseProjector</c> fanned out + concatenated
/// across every source. Read-only + poll-friendly (PR7 is a GET; live push is a later concern).
/// </summary>
public sealed record TaskRunPhasesResponse
{
    public required Guid RunId { get; init; }

    /// <summary>The run's overall status as its <c>WorkflowRunStatus</c> enum NAME (open string — never switched on).</summary>
    public required string RunStatus { get; init; }

    public required IReadOnlyList<RunPhase> Phases { get; init; }
}
