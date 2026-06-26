using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Re-run ONE branch of a top-level flow.map in a prior run (D7). Forks a new run that REUSES the N-1 sibling
/// branches (pre-seeded from the original, replayed by the engine's map machinery — no side-effect re-fire) and
/// re-runs <see cref="BranchIndex"/> + the map's downstream (the synthesizer re-runs over the new aggregate).
/// The new run inherits the original's definition + release hash + variable snapshot; lineage rides on
/// <c>ParentRunId</c> + the request causation.
///
/// <para>Tenancy: the original run must belong to the caller's current team (404 conflated). Refuses (before any
/// write): an unknown / non-top-level / non-map target, a branch index outside the original fan-out, an original
/// map that didn't complete successfully (no clean aggregate to reuse), or a branch body containing a
/// side-effecting / suspendable / nested-container node (v1 supports pure-compute branch bodies only). Returns
/// the new <c>WorkflowRun.Id</c>.</para>
/// </summary>
public sealed record RerunMapBranchCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid OriginalRunId { get; init; }

    /// <summary>The top-level flow.map node whose branch to re-run.</summary>
    public string MapNodeId { get; init; } = "";

    /// <summary>The 0-based element index of the branch to re-run; siblings are reused.</summary>
    public int BranchIndex { get; init; }

    /// <summary>
    /// Optional client-minted idempotency token. The UI mints ONE GUID per Rerun click and resends it verbatim on a
    /// retry, so a double-submit (or a retried HTTP call) returns the SAME fork instead of forking twice; a genuine
    /// re-rerun mints a new token. Null (a client that doesn't send one) opts out — each call forks independently.
    /// </summary>
    public Guid? OperationId { get; init; }
}
