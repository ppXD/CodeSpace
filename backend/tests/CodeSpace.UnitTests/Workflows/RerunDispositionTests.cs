using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the generic <see cref="RerunDispositions"/> seam that the from-node rerun gate
/// (<c>WorkflowService.IsRerunUnsupported</c>) and the map-branch policy (<see cref="RerunBranchBodyPolicy"/>) now
/// consult. The headline test is an EXHAUSTIVE drift-detector (Rule 12.5): over the FULL flag cube
/// (CanSuspend × IsSideEffecting × IsRerunnableWhenSuspendable × every NodeKind × every exempt-map case), the
/// disposition-based admit decision MUST equal the negation of the FROZEN boolean predicates — copied verbatim below.
/// The CONTAINER-BODY arm is still byte-identical to the original. The FROM-NODE arm encodes the P2.2 spec (the
/// agent.run re-stage opt-in is now admitted as a from-node root); the cube proves this change is SURGICAL — the new
/// from-node spec differs from the frozen pre-P2.2 predicate on EXACTLY the agent.run <c>ReStageExternalRun</c> cells,
/// and only by flipping refuse → admit. Every other cell is unchanged.
/// </summary>
[Trait("Category", "Unit")]
public class RerunDispositionTests
{
    private static NodeManifest Manifest(bool canSuspend, bool sideEffecting, bool rerunnableWhenSuspendable, NodeKind kind) => new()
    {
        DisplayName = "n",
        Category = "Test",
        Kind = kind,
        CanSuspend = canSuspend,
        IsSideEffecting = sideEffecting,
        IsRerunnableWhenSuspendable = rerunnableWhenSuspendable,
        ConfigSchema = SchemaBuilder.EmptyObject(),
        InputSchema = SchemaBuilder.EmptyObject(),
        OutputSchema = SchemaBuilder.EmptyObject(),
    };

    [Fact]
    public void The_disposition_gate_is_byte_identical_to_the_original_predicates_across_the_full_flag_cube()
    {
        var bools = new[] { false, true };
        var seenDispositions = new HashSet<RerunDisposition>();
        var cells = 0;
        var p2_2DeltaCells = 0;

        foreach (var canSuspend in bools)
        foreach (var sideEffecting in bools)
        foreach (var rerunnable in bools)
        foreach (var kind in Enum.GetValues<NodeKind>())
        {
            var m = Manifest(canSuspend, sideEffecting, rerunnable, kind);
            seenDispositions.Add(RerunDispositions.For(m));

            // FROM-NODE ROOT — across the exempt-map cases: the node IS the exempt branch-rerun target, is NOT, or no exemption.
            foreach (var (nodeId, exemptMapId) in new[] { ("n", "n"), ("n", "other"), ("n", (string?)null) })
            {
                RerunDispositions.Admits(m, RerunContext.FromNodeRoot, nodeId, exemptMapId)
                    .ShouldBe(!NewIsRerunUnsupported(m, nodeId, exemptMapId),
                        $"FromNodeRoot diverged at canSuspend={canSuspend} side={sideEffecting} rerunnable={rerunnable} kind={kind} exempt={exemptMapId ?? "null"}");

                // SURGICAL-CHANGE PROOF: the post-P2.2 from-node spec may differ from the frozen pre-P2.2 predicate
                // ONLY on the agent.run re-stage opt-in (ReStageExternalRun), and only by flipping refuse → admit.
                if (NewIsRerunUnsupported(m, nodeId, exemptMapId) != OldIsRerunUnsupported(m, nodeId, exemptMapId))
                {
                    RerunDispositions.For(m).ShouldBe(RerunDisposition.ReStageExternalRun,
                        $"P2.2 may only change the agent.run re-stage cell, but kind={kind} canSuspend={canSuspend} side={sideEffecting} rerunnable={rerunnable} changed");
                    OldIsRerunUnsupported(m, nodeId, exemptMapId).ShouldBeTrue("the changed cell was refused pre-P2.2");
                    NewIsRerunUnsupported(m, nodeId, exemptMapId).ShouldBeFalse("and is admitted post-P2.2");
                    p2_2DeltaCells++;
                }

                cells++;
            }

            // CONTAINER BODY — no exempt notion; still byte-identical to the original predicate (P2.2 changes nothing here).
            RerunDispositions.Admits(m, RerunContext.ContainerBody, nodeId: "n", exemptMapId: null)
                .ShouldBe(!OldIsRefusedAsBranchBody(m),
                    $"ContainerBody diverged at canSuspend={canSuspend} side={sideEffecting} rerunnable={rerunnable} kind={kind}");
            cells++;
        }

        // Guard against a vacuous pass: the cube must actually exercise every disposition bucket + a non-trivial cell count.
        seenDispositions.ShouldBe(Enum.GetValues<RerunDisposition>(), ignoreOrder: true, "the cube must reach every disposition bucket — else a derivation arm is untested");
        cells.ShouldBeGreaterThan(100, "the cube must be exhaustive over the full flag/kind/exempt space");
        p2_2DeltaCells.ShouldBeGreaterThan(0, "the P2.2 from-node delta (agent.run admitted as a root) must actually be exercised by the cube");
    }

    // ── Explicit per-disposition pins (legibility on top of the exhaustive cube) ──────

    [Fact]
    public void Agent_code_is_ReStageExternalRun_admitted_as_both_a_body_and_a_from_node_root()
    {
        var agentCode = Manifest(canSuspend: true, sideEffecting: false, rerunnableWhenSuspendable: true, NodeKind.Regular);

        RerunDispositions.For(agentCode).ShouldBe(RerunDisposition.ReStageExternalRun);
        RerunDispositions.Admits(agentCode, RerunContext.ContainerBody, "n", null).ShouldBeTrue("a map-branch body re-stages the agent run (the proven path)");
        RerunDispositions.Admits(agentCode, RerunContext.FromNodeRoot, "n", null).ShouldBeTrue("a from-node ROOT re-stages a fresh agent run on the forked run id (P2.2)");
    }

    [Fact]
    public void An_un_opted_suspendable_node_is_RefuseSuspendable_in_both_contexts()
    {
        var supervisorLike = Manifest(canSuspend: true, sideEffecting: false, rerunnableWhenSuspendable: false, NodeKind.Regular);

        RerunDispositions.For(supervisorLike).ShouldBe(RerunDisposition.RefuseSuspendable);
        RerunDispositions.Admits(supervisorLike, RerunContext.FromNodeRoot, "n", null).ShouldBeFalse();
        RerunDispositions.Admits(supervisorLike, RerunContext.ContainerBody, "n", null).ShouldBeFalse();
    }

    [Fact]
    public void A_map_is_refused_from_a_node_root_unless_it_is_the_exempt_branch_rerun_target()
    {
        var map = Manifest(canSuspend: false, sideEffecting: false, rerunnableWhenSuspendable: false, NodeKind.Map);

        RerunDispositions.Admits(map, RerunContext.FromNodeRoot, "themap", exemptMapId: "themap").ShouldBeTrue("the exempt map IS the branch-rerun target");
        RerunDispositions.Admits(map, RerunContext.FromNodeRoot, "themap", exemptMapId: "other").ShouldBeFalse("a non-exempt map is refused");
        RerunDispositions.Admits(map, RerunContext.FromNodeRoot, "themap", exemptMapId: null).ShouldBeFalse("the from-node path passes no exemption");
        RerunDispositions.Admits(map, RerunContext.ContainerBody, "themap", null).ShouldBeFalse("a nested map body is refused (the one-level scan invariant)");
    }

    [Fact]
    public void A_pure_node_admits_both_contexts_and_a_side_effecting_leaf_admits_both()
    {
        var pure = Manifest(canSuspend: false, sideEffecting: false, rerunnableWhenSuspendable: false, NodeKind.Regular);
        var sideEffecting = Manifest(canSuspend: false, sideEffecting: true, rerunnableWhenSuspendable: false, NodeKind.Regular);

        RerunDispositions.For(pure).ShouldBe(RerunDisposition.PureReExecute);
        RerunDispositions.For(sideEffecting).ShouldBe(RerunDisposition.SideEffectGuarded);

        foreach (var m in new[] { pure, sideEffecting })
        {
            RerunDispositions.Admits(m, RerunContext.FromNodeRoot, "n", null).ShouldBeTrue();
            RerunDispositions.Admits(m, RerunContext.ContainerBody, "n", null).ShouldBeTrue();
        }
    }

    // ── FROZEN truth tables (Rule 12.5) — independently re-derived from the intended spec, NOT copied from the impl. ──

    /// <summary>The from-node gate as it stood BEFORE P2.2 — every suspendable node refused. The drift baseline the
    /// surgical-change proof measures the P2.2 delta against.</summary>
    private static bool OldIsRerunUnsupported(NodeManifest m, string nodeId, string? exemptMapId) =>
        m.CanSuspend
        || (m.Kind == NodeKind.Map && nodeId != exemptMapId)
        || m.Kind is NodeKind.Loop or NodeKind.Try;

    /// <summary>The from-node gate spec AFTER P2.2 — a suspendable node is refused UNLESS it is the agent.run re-stage
    /// opt-in (<c>IsRerunnableWhenSuspendable &amp;&amp; !IsSideEffecting</c>), which is now admitted as a root. The
    /// container-kind disjuncts are unchanged.</summary>
    private static bool NewIsRerunUnsupported(NodeManifest m, string nodeId, string? exemptMapId) =>
        (m.CanSuspend && !(m.IsRerunnableWhenSuspendable && !m.IsSideEffecting))
        || (m.Kind == NodeKind.Map && nodeId != exemptMapId)
        || m.Kind is NodeKind.Loop or NodeKind.Try;

    private static bool OldIsRefusedAsBranchBody(NodeManifest m) =>
        (m.CanSuspend && !m.IsRerunnableWhenSuspendable)
        || (m.IsSideEffecting && m.CanSuspend)
        || m.Kind is NodeKind.Map or NodeKind.Loop or NodeKind.Try;
}
