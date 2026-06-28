using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the generic <see cref="RerunDispositions"/> seam that the from-node rerun gate
/// (<c>WorkflowService.IsRerunUnsupported</c>) and the map-branch policy (<see cref="RerunBranchBodyPolicy"/>) now
/// consult. The headline test is an EXHAUSTIVE byte-identity drift-detector (Rule 12.5): over the FULL flag cube
/// (CanSuspend × IsSideEffecting × IsRerunnableWhenSuspendable × every NodeKind × every exempt-map case), the
/// disposition-based admit decision MUST equal the negation of the FROZEN pre-refactor boolean predicates — copied
/// verbatim below. The refactor is a pure classification re-expression; this proves it changed ZERO behaviour.
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
                    .ShouldBe(!OldIsRerunUnsupported(m, nodeId, exemptMapId),
                        $"FromNodeRoot diverged at canSuspend={canSuspend} side={sideEffecting} rerunnable={rerunnable} kind={kind} exempt={exemptMapId ?? "null"}");
                cells++;
            }

            // CONTAINER BODY — no exempt notion.
            RerunDispositions.Admits(m, RerunContext.ContainerBody, nodeId: "n", exemptMapId: null)
                .ShouldBe(!OldIsRefusedAsBranchBody(m),
                    $"ContainerBody diverged at canSuspend={canSuspend} side={sideEffecting} rerunnable={rerunnable} kind={kind}");
            cells++;
        }

        // Guard against a vacuous pass: the cube must actually exercise every disposition bucket + a non-trivial cell count.
        seenDispositions.ShouldBe(Enum.GetValues<RerunDisposition>(), ignoreOrder: true, "the cube must reach every disposition bucket — else a derivation arm is untested");
        cells.ShouldBeGreaterThan(100, "the cube must be exhaustive over the full flag/kind/exempt space");
    }

    // ── Explicit per-disposition pins (legibility on top of the exhaustive cube) ──────

    [Fact]
    public void Agent_code_is_ReStageExternalRun_admitted_as_a_body_but_refused_as_a_from_node_root()
    {
        var agentCode = Manifest(canSuspend: true, sideEffecting: false, rerunnableWhenSuspendable: true, NodeKind.Regular);

        RerunDispositions.For(agentCode).ShouldBe(RerunDisposition.ReStageExternalRun);
        RerunDispositions.Admits(agentCode, RerunContext.ContainerBody, "n", null).ShouldBeTrue("a map-branch body re-stages the agent run (the proven path)");
        RerunDispositions.Admits(agentCode, RerunContext.FromNodeRoot, "n", null).ShouldBeFalse("a from-node ROOT stays refused until the root re-stage substrate lands (P2.2)");
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

    // ── FROZEN pre-refactor truth tables (Rule 12.5) — copied EXACTLY from the original predicate bodies. ──────────

    private static bool OldIsRerunUnsupported(NodeManifest m, string nodeId, string? exemptMapId) =>
        m.CanSuspend
        || (m.Kind == NodeKind.Map && nodeId != exemptMapId)
        || m.Kind is NodeKind.Loop or NodeKind.Try;

    private static bool OldIsRefusedAsBranchBody(NodeManifest m) =>
        (m.CanSuspend && !m.IsRerunnableWhenSuspendable)
        || (m.IsSideEffecting && m.CanSuspend)
        || m.Kind is NodeKind.Map or NodeKind.Loop or NodeKind.Try;
}
