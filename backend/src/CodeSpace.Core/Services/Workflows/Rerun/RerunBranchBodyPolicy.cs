using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Rerun;

/// <summary>
/// The pure D7-5 policy deciding WHICH node types may appear as a re-run <c>flow.map</c> branch BODY node. It is a
/// FAIL-CLOSED ALLOWLIST, not a blanket relaxation: a body node is REFUSED unless it is provably safe to re-run.
///
/// <para><b>ADMITTED</b> (the predicate returns false):
/// <list type="bullet">
///   <item>PURE compute / read nodes — neither side-effecting nor suspendable; re-run freely.</item>
///   <item>PURELY side-effecting nodes (<c>IsSideEffecting</c> &amp;&amp; !<c>CanSuspend</c> — git writes, http POST, issue
///         ops, <c>agent.run_command</c>) — each routes through the D7-3 <c>RerunSideEffectGate</c> at runtime: on a
///         rerun the engine parks it on an Approval wait (approve → fire once; reject → skip), so it never SILENTLY
///         re-fires.</item>
///   <item><c>agent.code</c> SPECIFICALLY — <c>CanSuspend</c> &amp;&amp; <see cref="NodeManifest.IsRerunnableWhenSuspendable"/>
///         &amp;&amp; !<c>IsSideEffecting</c> — a re-run branch re-stages a FRESH <c>AgentRun</c> under the branch's
///         iteration key (mechanically identical to the shipped original-run map durable resume), with NO node-level
///         re-fire gate. This is the intended "execute-again" semantics: re-running an agent branch RE-RUNS the agent,
///         which may repeat its actions (a fresh fork run has a fresh tool-call ledger, so the agent's git push /
///         open_pr can repeat). The operator's click-to-rerun IS the node-level human intent; the agent's OWN
///         irreversible tool calls stay governed at the TOOL level (the MCP governance ledger + AlwaysRequiresApproval
///         tools like merge_pr remain human-gated inside the agent). A node-level agent-rerun approval gate is a
///         deferred option, not a v1 requirement. The <c>&amp;&amp; !IsSideEffecting</c> half holds because agent.code is
///         not flagged side-effecting; were it ever both-flagged, the both-flag arm below would refuse it.</item>
/// </list></para>
///
/// <para><b>REFUSED</b> (the predicate returns true):
/// <list type="bullet">
///   <item>Any OTHER suspendable node — a <c>CanSuspend</c> node that did NOT opt in. <c>flow.wait_*</c> strand the fork
///         (a fresh wait is minted but no human / webhook re-issues the original signal); <c>flow.subworkflow</c> forks
///         an entire ungated child run whose side effects bypass this scan; <c>agent.supervisor</c> has a distinct
///         per-turn wait shape; <c>flow.sleep</c> is unanalyzed. Each stays refused until its substrate is proven.</item>
///   <item>A node that is BOTH side-effecting AND suspendable (<c>chat.post_message</c>) — the D7-3 gate runs
///         unconditionally at the top of every walk and discriminates only on resume-payload presence + the
///         <c>approved</c> field, so a post-then-suspend node would FIRE its effect on the gate-approved walk and then
///         be mis-skipped on its own resume (the gate sees the node's own resume payload, no <c>approved</c> key →
///         not-approved → node.skipped). Belt-and-suspenders: refused both here AND by the no-opt-in arm.</item>
///   <item>A nested container (<c>Map</c> / <c>Loop</c> / <c>Try</c>) — a separate hard problem (its body sub-graph has
///         its own iteration keying + seed/replay model).</item>
/// </list></para>
///
/// <para><b>LOAD-BEARING:</b> the caller scans only DIRECT body nodes (<c>ParentId == mapNodeId</c>), one level deep.
/// That scan is COMPLETE only because the container arm refuses every direct <c>Map</c>/<c>Loop</c>/<c>Try</c> — the
/// only way to nest a deeper child — so no descendant of an admitted body can exist. A future nested-container lift
/// MUST add recursion before dropping the container arm.</para>
/// </summary>
public static class RerunBranchBodyPolicy
{
    /// <summary>True iff a node with this manifest is REFUSED as a re-run map-branch body (fail-closed allowlist). Delegates to the generic <see cref="RerunDispositions"/> seam (byte-identical to the prior un-opted-suspendable / both-flagged / nested-container disjunction).</summary>
    public static bool IsRefusedAsBranchBody(NodeManifest manifest) =>
        !RerunDispositions.Admits(manifest, RerunContext.ContainerBody, nodeId: string.Empty, exemptMapId: null);
}
