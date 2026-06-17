namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Thrown when a from-node rerun (D7) is refused because its RE-RUN closure contains an effectful node — one that
/// would re-fire a side effect (git write / chat post / http / agent.run_command), suspend (agent.code /
/// subworkflow / supervisor — <c>CanSuspend</c>), or re-run a whole effectful body (a Map/Loop/Try container).
/// Slice-1 is FAIL-CLOSED: re-running such a node un-gated could open a duplicate PR, post twice, or re-bill an
/// agent, so the rerun is refused entirely until the require-approval gate (D7-3) lands. A side-effecting node
/// that is UPSTREAM (kept + reused, never re-executed) is allowed. The global filter maps this to 422 with the
/// offending node id(s) so the operator knows exactly which node blocks the rerun.
/// </summary>
public sealed class RerunBlockedBySideEffectException : Exception
{
    public IReadOnlyList<string> BlockedNodeIds { get; }

    public RerunBlockedBySideEffectException(IReadOnlyList<string> blockedNodeIds)
        : base($"Re-running from this node would re-execute effectful node(s) [{string.Join(", ", blockedNodeIds)}] that could repeat a side effect; this is not yet supported (approval-gated rerun is a follow-up).")
    {
        BlockedNodeIds = blockedNodeIds;
    }
}
