using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// ON-DEMAND re-inflation of the offloaded values in a run detail's node outputs — the read-side counterpart of
/// <see cref="NodeOutputArtifacts.OffloadLargeAsync"/>, for the callers that actually read an output's CONTENT.
///
/// <para>The ledger stores an oversize output value as a compact <c>{"$artifact_ref":…}</c> pointer, so a run detail
/// carries the pointer, not the bytes. <c>WorkflowService.GetRunAsync</c> — the shared read behind the Journal, the
/// Room, the phase board and the run-detail API — used to resolve EVERY cell of every read, which cost one whole-blob
/// fetch + SHA-256 verification per offloaded cell on a read the Journal walk performs several times per turn to reach
/// output values it almost never needs: its plan facts read a map's plan out of ONE producer cell, and everything else
/// those projectors read is a top-level SCALAR — a map's count/failed, a planner's model/tokens/cost — that
/// per-property offload never reaches. It no longer resolves anything; a caller that needs content asks for it here,
/// for the cells it actually reads.</para>
///
/// <para>Bytes still come through <see cref="IArtifactStore.GetBytesAsync"/>, which verifies every read against the
/// store's own sha256/size claim and throws rather than return unverified bytes. That verification is unchanged and
/// stays where it belongs — in the store — so moving the fetch off the shared read moves WHEN bytes are fetched, never
/// WHETHER they are proven. Resolution is fail-safe the same way <see cref="NodeOutputArtifacts.ResolveAsync"/> is: a
/// ref whose artifact is missing / cross-team is left verbatim rather than dropped.</para>
/// </summary>
public interface IRunNodeOutputInflater
{
    /// <summary>Inflate the offloaded output values of EVERY cell — what the run-detail API returns, so an operator inspecting a step sees the real value rather than a pointer.</summary>
    Task<WorkflowRunDetail> InflateAsync(WorkflowRunDetail run, Guid teamId, CancellationToken cancellationToken);

    /// <summary>Inflate only the cells whose <c>NodeId</c> is in <paramref name="nodeIds"/> — for a caller that reads one named node's output (a map's planner) instead of the whole run's.</summary>
    Task<WorkflowRunDetail> InflateAsync(WorkflowRunDetail run, Guid teamId, IReadOnlySet<string> nodeIds, CancellationToken cancellationToken);
}
