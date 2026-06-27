using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Re-run a SET of a top-level flow.map's branches in ONE forked run — the generic form of
/// <see cref="RerunMapBranchCommand"/> (which is the <c>|BranchIndices| == 1</c> case). The chosen branches re-run
/// fresh while every other reusable sibling is replayed from the original (no side-effect re-fire), then the map
/// re-aggregates over the mix and its downstream re-runs. The UI's "Rerun all failed items" maps here.
///
/// <para>Same fail-closed gates + tenancy as <see cref="RerunMapBranchCommand"/>, plus an empty set is rejected.
/// <see cref="OperationId"/> is the optional client-minted idempotency token (one per click → a double-submit /
/// retry returns the SAME fork); the active-rerun lease refuses a concurrent rerun that overlaps an in-flight one.
/// Returns the new <c>WorkflowRun.Id</c>.</para>
/// </summary>
public sealed record RerunMapBranchesCommand : ICommand<Guid>, IRequireTeamMembership
{
    public Guid OriginalRunId { get; init; }

    /// <summary>The top-level flow.map node whose branches to re-run.</summary>
    public string MapNodeId { get; init; } = "";

    /// <summary>The 0-based element indices of the branches to re-run; every other sibling is reused.</summary>
    public IReadOnlyList<int> BranchIndices { get; init; } = [];

    /// <summary>Optional client-minted idempotency token (see <see cref="RerunMapBranchCommand.OperationId"/>).</summary>
    public Guid? OperationId { get; init; }
}
