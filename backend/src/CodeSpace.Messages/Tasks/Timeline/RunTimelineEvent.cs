namespace CodeSpace.Messages.Tasks.Timeline;

/// <summary>
/// One event on a run's narrative timeline — a run/node lifecycle step, a supervisor decision, an agent's file
/// edit, a parked decision, … The shape is a FLAT, source-agnostic noun the UI renders as a chronological story:
/// it never switches on <see cref="Kind"/> (an OPEN string), only on <see cref="Severity"/> (the closed render
/// tone). Multiple <c>IRunTimelineSource</c> contribute events over the SAME run id and the projector merges them
/// by <see cref="OccurredAt"/>; <see cref="SourceKey"/> records which source produced the event (provenance + the
/// merge tie-break). This is a pure READ projection over the EXISTING ledgers — no new substrate, no schema change.
/// </summary>
public sealed record RunTimelineEvent
{
    /// <summary>Stable per-run id (e.g. <c>record-{sequence}</c>, <c>decision-{sequence}</c>) — the UI's React key + the merge tie-break.</summary>
    public required string Id { get; init; }

    /// <summary>OPEN event-kind string (e.g. <c>run.started</c>, <c>node.failed</c>, <c>supervisor.spawn</c>). NEVER switched on.</summary>
    public required string Kind { get; init; }

    /// <summary>Human one-line title (e.g. "Run started", "code failed", "Spawned 3 agents").</summary>
    public required string Title { get; init; }

    /// <summary>An optional one-line detail (an error message, a wait reason, an answer). Null when none.</summary>
    public string? Summary { get; init; }

    /// <summary>The closed render tone — Info / Success / Warning / Error.</summary>
    public required TimelineSeverity Severity { get; init; }

    /// <summary>The closed narrative prominence — a <c>Milestone</c> shows in the story by default; a <c>Detail</c> folds into a "N steps" disclosure. Defaults to <c>Milestone</c> so an un-leveled source is never silently hidden.</summary>
    public TimelineLevel Level { get; init; } = TimelineLevel.Milestone;

    /// <summary>When the event occurred — the primary chronological sort key.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The source's own monotonic order (e.g. the ledger Sequence) — the same-<see cref="OccurredAt"/> tie-break so two events in one tick keep their true ledger order. 0 when the source has none.</summary>
    public long Order { get; init; }

    /// <summary>The node this event belongs to, when applicable (null for run-level events).</summary>
    public string? NodeId { get; init; }

    /// <summary>The agent run this event belongs to, when applicable (null otherwise).</summary>
    public string? AgentRunId { get; init; }

    /// <summary>The agent run's CELL key (its <c>IterationKey</c> — e.g. <c>map#0</c>, <c>boss#turn1#0</c>, or a reviewer run's <c>…#review</c> / <c>#plan-review</c> suffix key), when applicable. Null for non-agent events / a keyless run. Lets a downstream describer classify by the run's ROLE without a database read.</summary>
    public string? IterationKey { get; init; }

    /// <summary>Which <c>IRunTimelineSource</c> produced this event (its SourceKey) — provenance + the merge tie-break.</summary>
    public required string SourceKey { get; init; }
}
