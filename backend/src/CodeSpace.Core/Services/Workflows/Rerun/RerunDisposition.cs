using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Rerun;

/// <summary>The rerun call context a disposition is evaluated against — a from-node ROOT target vs a container BODY node. The container-kind admit rule differs by context (the exempt map vs no nested container).</summary>
public enum RerunContext
{
    FromNodeRoot,
    ContainerBody,
}

/// <summary>
/// The rerun classification a node-kind earns from its manifest — the SUSPEND / SIDE-EFFECT axis only. The orthogonal
/// CONTAINER-kind axis (Map / Loop / Try) is applied per-context in <see cref="RerunDispositions.Admits"/>, exactly as
/// the two original gate predicates treated container-kind as a SEPARATE disjunct from <c>CanSuspend</c>.
/// </summary>
public enum RerunDisposition
{
    /// <summary>A deterministic / read-only node — re-run verbatim. Admits both a from-node root and a container body.</summary>
    PureReExecute,

    /// <summary>A side-effecting (but non-suspendable) leaf — admits both contexts; the runtime D7-3 approval gate fires at execution, not at the rerun gate.</summary>
    SideEffectGuarded,

    /// <summary>An agent.code node — the <c>IsRerunnableWhenSuspendable</c> opt-in: its external agent run is RE-STAGED. Admitted as a container BODY today (the proven map-branch re-stage); a from-node ROOT stays refused until the root re-stage substrate lands (P2.2 — the single <see cref="RerunContext.FromNodeRoot"/> arm).</summary>
    ReStageExternalRun,

    /// <summary>Any other suspendable node (supervisor / subworkflow / wait_* / sleep / chat.post_message) with no built re-stage — refused in BOTH contexts (fail-closed).</summary>
    RefuseSuspendable,
}

/// <summary>
/// The single source of truth for "can a rerun target this node, in this context" — the switch key the from-node gate
/// (<c>WorkflowService.IsRerunUnsupported</c>) and the map-branch policy (<see cref="RerunBranchBodyPolicy"/>) consult
/// instead of re-deriving the raw <c>CanSuspend</c> / <c>IsSideEffecting</c> / <c>IsRerunnableWhenSuspendable</c>
/// disjunction at each site. BYTE-IDENTICAL to those predicates (proven exhaustively over the full flag cube + every
/// live node kind); the win is one classification per node-kind and a SINGLE place — the
/// <see cref="RerunContext.FromNodeRoot"/> arm for <see cref="RerunDisposition.ReStageExternalRun"/> — for P2.2 to admit
/// a new from-node root, instead of duplicating the from-node-vs-body distinction across two predicates.
/// </summary>
public static class RerunDispositions
{
    /// <summary>The disposition a node-kind earns from its manifest — the suspend / side-effect axis (the container-kind axis is applied in <see cref="Admits"/>). A <c>CanSuspend</c> node is the agent.code re-stage opt-in or a fail-closed refusal; a non-suspendable node is side-effect-guarded or pure.</summary>
    public static RerunDisposition For(NodeManifest manifest) =>
        manifest.CanSuspend
            ? (manifest.IsRerunnableWhenSuspendable && !manifest.IsSideEffecting ? RerunDisposition.ReStageExternalRun : RerunDisposition.RefuseSuspendable)
            : manifest.IsSideEffecting ? RerunDisposition.SideEffectGuarded : RerunDisposition.PureReExecute;

    /// <summary>
    /// Whether a rerun ADMITS this node in the given context — the disposition axis AND the orthogonal container-kind
    /// axis, ANDed (mirroring the original predicates' separate disjuncts). A from-node ROOT admits a Map ONLY when it
    /// is the exempt branch-rerun target (<paramref name="exemptMapId"/>); a container BODY admits no nested
    /// Map / Loop / Try (the one-level body scan is complete only because of this).
    /// </summary>
    public static bool Admits(NodeManifest manifest, RerunContext context, string nodeId, string? exemptMapId)
    {
        var dispositionAdmits = context switch
        {
            RerunContext.FromNodeRoot => For(manifest) is RerunDisposition.PureReExecute or RerunDisposition.SideEffectGuarded,
            RerunContext.ContainerBody => For(manifest) is RerunDisposition.PureReExecute or RerunDisposition.SideEffectGuarded or RerunDisposition.ReStageExternalRun,
            _ => false,
        };

        if (!dispositionAdmits) return false;

        var containerRefuses = context switch
        {
            RerunContext.FromNodeRoot => manifest.Kind is NodeKind.Loop or NodeKind.Try || (manifest.Kind == NodeKind.Map && nodeId != exemptMapId),
            RerunContext.ContainerBody => manifest.Kind is NodeKind.Map or NodeKind.Loop or NodeKind.Try,
            _ => true,
        };

        return !containerRefuses;
    }
}
