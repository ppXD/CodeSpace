namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Thrown when a from-node rerun (D7) is refused because its RE-RUN closure contains a node whose re-execution
/// isn't supported yet: a SUSPENDABLE node (<c>CanSuspend</c> — agent.code / subworkflow / supervisor, which
/// would re-stage an agent run / child / decision) or a CONTAINER (Map/Loop/Try, which would re-run its whole
/// body atomically). Fail-closed.
///
/// <para>A SIDE-EFFECTING node (git write / chat post / http) in the closure is NOT refused — the engine
/// approval-gates it at runtime (D7-3): it suspends on an Approval wait so the operator confirms the re-fire
/// (or rejects to skip it). A side-effecting node that is UPSTREAM (kept + reused, never re-executed) is also
/// fine. The global filter maps THIS exception to 422 with the offending node id(s) so the operator knows
/// exactly which node blocks the rerun.</para>
/// </summary>
public sealed class RerunBlockedByUnsupportedNodeException : Exception
{
    public IReadOnlyList<string> BlockedNodeIds { get; }

    public RerunBlockedByUnsupportedNodeException(IReadOnlyList<string> blockedNodeIds)
        : base($"Re-running from this node would re-execute node(s) [{string.Join(", ", blockedNodeIds)}] whose rerun isn't supported yet (a suspendable agent/subworkflow/supervisor node, or a Map/Loop/Try container). Re-run from a later node, or replay the whole run.")
    {
        BlockedNodeIds = blockedNodeIds;
    }
}
