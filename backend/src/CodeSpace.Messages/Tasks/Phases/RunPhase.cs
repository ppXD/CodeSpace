namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// One row in a task run's background-tasks UI phase tree — a structural node phase (a map fan-out, an agent.run
/// step), a supervisor decision phase (plan/spawn/stop), or any future source's contribution. The shape is a
/// FLAT, source-agnostic noun: the UI renders the merged list sorted by <see cref="Order"/> and never switches on
/// <see cref="Kind"/> (an OPEN string — 'plan'/'spawn'/'map'/'node'/'agent'/'review'/'wait'/…). The ONLY closed
/// axis is <see cref="Status"/> (the render vocabulary). Multiple <c>IRunPhaseSource</c> contribute rows
/// over the SAME run id and the projector concats + stable-sorts them — <see cref="SourceKey"/> records which
/// source produced this row (the merge tie-break + provenance), <see cref="Order"/> is the cross-source sort key.
/// </summary>
public sealed record RunPhase
{
    /// <summary>Stable per-run id for this phase (e.g. the node id, or <c>decision-{sequence}</c>). The UI's React key.</summary>
    public required string Id { get; init; }

    /// <summary>Human label for the chip (e.g. "Fan out", "Plan", "Spawn 3 agents").</summary>
    public required string Label { get; init; }

    /// <summary>OPEN phase-kind string — 'plan'/'spawn'/'map'/'node'/'agent'/'review'/'wait'/etc. NEVER switched on.</summary>
    public required string Kind { get; init; }

    public required PhaseStatus Status { get; init; }

    /// <summary>Cross-source merge/sort key — the projector stable-sorts the merged list by this (tie-break StartedAt then SourceKey).</summary>
    public required int Order { get; init; }

    /// <summary>The agent runs this phase fanned out to (a map's branches, a spawn's children, an agent node's one agent). Empty for a structural-only phase.</summary>
    public IReadOnlyList<PhaseAgentRef> Agents { get; init; } = Array.Empty<PhaseAgentRef>();

    public PhaseMetrics Metrics { get; init; } = new();

    /// <summary>An optional one-line detail (e.g. an ask_human question + answer). Null when none.</summary>
    public string? Summary { get; init; }

    /// <summary>Which <see cref="IRunPhaseSource"/> produced this row (the <c>SourceKey</c>) — provenance + the sort tie-break.</summary>
    public required string SourceKey { get; init; }

    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
