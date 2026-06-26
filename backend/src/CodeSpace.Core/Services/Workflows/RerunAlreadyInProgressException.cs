namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Thrown when a map-branch rerun would re-run a branch that ALREADY has an in-flight rerun (an active
/// <c>WorkflowRerunLease</c> over the same <c>(OriginalRunId, MapNodeId, BranchIndex)</c>). A concurrent,
/// distinct-operation rerun whose branch set OVERLAPS an in-progress one is refused rather than allowed to
/// double-fire a side-effecting branch body. Distinct from the OperationId idempotency path (a SAME-token
/// resubmit dedups to the prior fork and never reaches the lease). The global exception filter maps it to 409.
/// </summary>
public sealed class RerunAlreadyInProgressException : Exception
{
    public RerunAlreadyInProgressException(Guid originalRunId, string mapNodeId, IReadOnlyCollection<int> branchIndices)
        : base($"A rerun of map '{mapNodeId}' branch(es) {string.Join(", ", branchIndices)} in run {originalRunId} is already in progress; wait for it to finish before rerunning the same branch.")
    {
        OriginalRunId = originalRunId;
        MapNodeId = mapNodeId;
        BranchIndices = branchIndices;
    }

    public Guid OriginalRunId { get; }
    public string MapNodeId { get; }
    public IReadOnlyCollection<int> BranchIndices { get; }
}
