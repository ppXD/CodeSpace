using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Constants;

namespace CodeSpace.Core.Services.Workflows.Rerun;

/// <summary>
/// The pure decision logic for the from-node rerun side-effect gate (D7-3). On a from-node RERUN, a re-run
/// side-effecting node must not SILENTLY re-fire its effect (a duplicate PR, a second chat post, a re-billed
/// call): the engine parks it on an Approval wait so a human confirms the re-fire (approve → run it; reject →
/// skip it). On a normal / replay run a side-effecting node fires freely — that's its first-time intent — so the
/// gate is conditioned on the run's source being <c>rerun</c>.
///
/// <para>Kept as a registry-free / engine-free static so the truth table is unit-testable; the engine calls it
/// inside the per-node execution path. This gate therefore governs only PURELY side-effecting nodes (IsSideEffecting
/// but NOT CanSuspend — git writes / http POST / git issue ops). A node that is BOTH side-effecting AND suspendable
/// (e.g. <c>chat.post_message</c> with waitForResponse), a CanSuspend node, or a container never reaches this gate —
/// the rerun staging refuses them up front (<see cref="RerunBlockedByUnsupportedNodeException"/>) — and reused
/// upstream cells are pre-seeded + settled so they never execute.</para>
/// </summary>
public static class RerunSideEffectGate
{
    /// <summary>The synthetic Approval-wait payload kind, so the run-detail UI can tell a side-effect gate apart from an authored <c>flow.wait_approval</c>.</summary>
    public const string PayloadKind = "rerun_side_effect_gate";

    /// <summary>True iff this node must be human-gated before it executes: a side-effecting node on a from-node rerun.</summary>
    public static bool ShouldGate(string? runSourceType, NodeManifest manifest) =>
        runSourceType == WorkflowRunSourceTypes.Rerun && manifest.IsSideEffecting;

    /// <summary>
    /// Read the operator's decision from the resolved Approval-wait payload
    /// (<c>{ approved, comment, by, resumed_at }</c>, written by <c>ApproveRunAsync</c>). Fail-closed:
    /// anything that isn't an explicit <c>approved: true</c> counts as NOT approved (→ skip the node).
    /// </summary>
    public static bool IsApproved(JsonElement resumePayload) =>
        resumePayload.ValueKind == JsonValueKind.Object
        && resumePayload.TryGetProperty("approved", out var approved)
        && approved.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Build the suspension token that parks the node on a human Approval wait. The correlation token is
    /// per-node so a multi-side-effect rerun parks one independent wait per node; the payload carries a
    /// human-readable prompt + the <see cref="PayloadKind"/> marker for the UI.
    /// </summary>
    public static SuspensionToken BuildApprovalToken(string nodeId, string displayName) =>
        new()
        {
            Kind = WorkflowWaitKinds.Approval,
            CorrelationToken = $"rerun-gate::{nodeId}",
            Payload = JsonSerializer.SerializeToElement(new
            {
                kind = PayloadKind,
                node = nodeId,
                message = $"Re-running '{displayName}' will RE-FIRE its side effect. Approve to proceed, or reject to skip this node.",
            }),
        };
}
