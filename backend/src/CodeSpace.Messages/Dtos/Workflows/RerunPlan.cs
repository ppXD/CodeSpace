namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// The pure plan for a from-node rerun (D7): which TOP-LEVEL nodes RE-RUN — the chosen node plus its transitive
/// forward closure over the top-level edge set (handle-agnostic) — vs which are KEPT (the complement, NOT
/// forward-reachable from the chosen node). KEPT nodes are the candidates to pre-seed from the original run's
/// settled cells so the engine REUSES their outputs instead of re-running them.
///
/// <para>Registry-free + DB-free: derived purely from the frozen <see cref="WorkflowDefinition"/> by
/// <c>RerunFromNodePlanner</c> so it unit-tests exhaustively over every graph shape. The wrapping service
/// intersects <see cref="KeptNodeIds"/> with the original run's actually-settled cells (the real pre-seed set)
/// and scans <see cref="ReRunNodeIds"/> for effectful nodes (the fail-closed gate) — both need the DB / node
/// registry and so live one layer out.</para>
/// </summary>
public sealed record RerunPlan
{
    /// <summary>The chosen from-node + every top-level node forward-reachable from it (over EVERY outgoing handle). These RE-RUN.</summary>
    public required IReadOnlySet<string> ReRunNodeIds { get; init; }

    /// <summary>The top-level complement (NOT in <see cref="ReRunNodeIds"/>). Candidates to pre-seed from the original's settled cells so they are reused, never re-run.</summary>
    public required IReadOnlySet<string> KeptNodeIds { get; init; }
}
