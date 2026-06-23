namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// A PRE-RESOLVED session binding for a new run — the only thing a run starter needs in order to stamp
/// <c>WorkflowRun.SessionId</c> / <c>SessionTurnIndex</c>. It carries an already-decided answer, never the
/// inputs to decide it.
///
/// <para>Resolver separation (the load-bearing rule): deciding WHICH session + turn a run belongs to —
/// PR-aggregate vs. scheduled-thread vs. manual follow-up vs. child/replay inheritance — is the upstream
/// resolver's job (task launch / provider matcher / replay service). The starters understand NO business
/// semantics; they only WRITE these two pre-resolved fields. A <c>null</c> assignment at a creation site is a
/// session-less run — the default, byte-identical to pre-session behaviour.</para>
/// </summary>
public sealed record SessionAssignment
{
    /// <summary>The owning <c>WorkSession</c> this run is one turn of.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>
    /// The run's 1-based turn ordinal — set ONLY for a top-level, user-visible turn. NULL for an inherited
    /// child / sub-workflow / replay / rerun run that rides the same session but consumes no new turn (it
    /// attaches to the timeline via <c>ParentRunId</c> instead).
    /// </summary>
    public required int? TurnIndex { get; init; }
}
