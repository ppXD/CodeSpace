using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Re-run a prior run STARTING FROM a chosen node (D7). Forks a new run that REUSES the upstream cells
/// (pre-seeded from the original) and re-runs <see cref="FromNodeId"/> + its transitive downstream. The new run
/// inherits the original's definition (snapshot inline OR pinned authored version), release hash, and variable
/// snapshot; lineage rides on <c>ParentRunId</c> + the request causation.
///
/// <para>Tenancy: the original run must belong to the caller's current team (404 conflated with not-yours).
/// Refuses (before any write): an unknown / container-internal from-node, a re-run closure containing a node
/// whose rerun isn't supported yet (a suspendable agent/subworkflow/supervisor node, or a Map/Loop/Try
/// container), or an upstream node that didn't settle reusably. A SIDE-EFFECTING node in the closure is NOT
/// refused — the engine approval-gates it at runtime (it suspends on an Approval wait; approve to re-fire,
/// reject to skip). Returns the new <c>WorkflowRun.Id</c>.</para>
/// </summary>
public sealed record RerunRunFromNodeCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid OriginalRunId { get; init; }

    /// <summary>The top-level node to re-run from. It + everything forward-reachable from it re-runs; everything else is reused.</summary>
    public string FromNodeId { get; init; } = "";
}
