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
///   <item><c>flow.subworkflow</c> (D2) — the same <c>IsRerunnableWhenSuspendable</c> &amp;&amp; !<c>IsSideEffecting</c>
///         opt-in: a re-run branch re-executes the node, staging a FRESH child run under the branch's fork
///         (mechanically identical to the agent.code re-stage). Its child's side effects are governed WITHIN the child
///         (the same "execute-again" semantics), not by this scan — so the node itself is not flagged side-effecting.</item>
///   <item><c>flow.sleep</c> (D3) — the same opt-in, and the SIMPLEST re-stage: the "external run" is just the engine's
///         OWN <c>Timer</c> wait, minted keyed to the fork's run id + a fresh wait id and self-woken by the scheduled
///         <c>ResumeWaitAsync</c>. Nothing external is re-issued (contrast the wait/decision nodes below), the original
///         run's timer is untouched, and a delay has no side effects — so it is not flagged side-effecting.</item>
/// </list></para>
///
/// <para><b>REFUSED</b> (the predicate returns true):
/// <list type="bullet">
///   <item>Any OTHER suspendable node — a <c>CanSuspend</c> node that did NOT opt in, each fail-closed for a concrete
///         reason (NOT "unanalyzed"): the EXTERNAL-signal waits STRAND the fork because re-executing mints a fresh
///         pending wait that nobody re-issues — <c>flow.wait_approval</c> needs a human to re-approve,
///         <c>flow.wait_callback</c> needs the external caller to re-POST the new token, and <c>flow.wait_action</c>
///         needs the upstream producer to re-emit the signal to the new correlation token; none happens on a rerun, so
///         the fork would park forever. <c>flow.decision</c> does NOT strand (it parks on a BOUNDED wait that self-wakes
///         to its default at the deadline), but re-executing it either re-prompts a human or applies that STALE DEFAULT
///         — a different answer than the original decision, so it is not a faithful re-stage (unlike <c>flow.sleep</c>,
///         whose timer resumes with no semantic change). Making the external-signal waits rerunnable needs a signal
///         RE-ISSUE substrate (the separate "suspend-and-wake" track), not this scan. <c>agent.supervisor</c> is refused
///         here too (its re-stage is a ledger-replay turn loop, a distinct larger substrate).</item>
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
