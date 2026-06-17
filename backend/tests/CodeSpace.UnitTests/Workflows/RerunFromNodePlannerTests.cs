using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Rerun;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Exhaustive shape coverage for <see cref="RerunFromNodePlanner"/> — the pure (registry-free, DB-free) graph
/// math behind D7 from-node rerun. Pins the RE-RUN closure (chosen node + transitive forward reachability over
/// the top-level edge set, HANDLE-AGNOSTIC) vs the KEPT complement (pre-seed candidates) across linear,
/// strict-upstream, diamond/parallel, branch (both handles), container-as-target, container-internal-never-kept,
/// terminal, root (≡ replay), unknown-node, container-internal-rejected, defensive-cycle, and disconnected-island.
/// </summary>
[Trait("Category", "Unit")]
public class RerunFromNodePlannerTests
{
    [Fact]
    public void Linear_from_middle_reruns_self_and_downstream_keeps_upstream()
    {
        var plan = RerunFromNodePlanner.Plan(Def([N("A"), N("B"), N("C"), N("D")], E("A", "B"), E("B", "C"), E("C", "D")), "C");

        plan.ReRunNodeIds.ShouldBe(new[] { "C", "D" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
    }

    [Fact]
    public void Keeps_every_strict_upstream_node_including_the_root()
    {
        var plan = RerunFromNodePlanner.Plan(Def([N("A"), N("B"), N("C")], E("A", "B"), E("B", "C")), "C");

        plan.ReRunNodeIds.ShouldBe(new[] { "C" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBe(new[] { "A", "B" }, ignoreOrder: true, "a root with everything downstream of fromNode is still KEPT");
    }

    [Fact]
    public void Diamond_from_one_arm_reruns_the_join_but_keeps_the_sibling_arm()
    {
        // A→B, A→C, B→D, C→D; rerun from B. D is reachable from B so it RE-RUNS even though C also feeds it —
        // the case a naive "reset everything after fromNode in topo order" gets wrong (it would keep D or drop C).
        var plan = RerunFromNodePlanner.Plan(
            Def([N("A"), N("B"), N("C"), N("D")], E("A", "B"), E("A", "C"), E("B", "D"), E("C", "D")), "B");

        plan.ReRunNodeIds.ShouldBe(new[] { "B", "D" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBe(new[] { "A", "C" }, ignoreOrder: true, "the sibling arm C is reused; D re-runs over the reused C output");
    }

    [Fact]
    public void Branch_node_reruns_both_handle_targets_handle_agnostic()
    {
        // A re-run branch may flip its decision, so BOTH the true and false targets must be in the re-run set.
        var plan = RerunFromNodePlanner.Plan(
            Def([N("branch"), N("T"), N("F")], E("branch", "T", "true"), E("branch", "F", "false")), "branch");

        plan.ReRunNodeIds.ShouldBe(new[] { "branch", "T", "F" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBeEmpty();
    }

    [Fact]
    public void Container_as_fromNode_is_an_atomic_top_level_unit()
    {
        // A→map→C; map owns a body {bstart,b1}. Rerun from map: top-level re-run is {map,C}; the traversal stops
        // at map because body nodes aren't top-level (the container re-runs its whole body itself).
        var plan = RerunFromNodePlanner.Plan(
            Def([N("A"), N("map"), N("C"), N("bstart", parent: "map"), N("b1", parent: "map")],
                E("A", "map"), E("map", "C"), E("bstart", "b1")), "map");

        plan.ReRunNodeIds.ShouldBe(new[] { "map", "C" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBe(new[] { "A" }, ignoreOrder: true);
    }

    [Fact]
    public void Kept_set_is_top_level_only_never_container_internal_cells()
    {
        var plan = RerunFromNodePlanner.Plan(
            Def([N("A"), N("map"), N("C"), N("bstart", parent: "map"), N("b1", parent: "map")],
                E("A", "map"), E("map", "C"), E("bstart", "b1")), "C");

        plan.KeptNodeIds.ShouldBe(new[] { "A", "map" }, ignoreOrder: true, "KEPT is top-level cells only — never the container's body nodes");
        plan.ReRunNodeIds.ShouldBe(new[] { "C" }, ignoreOrder: true);
    }

    [Fact]
    public void Terminal_as_fromNode_reruns_only_the_terminal()
    {
        var plan = RerunFromNodePlanner.Plan(Def([N("A"), N("B"), N("term")], E("A", "B"), E("B", "term")), "term");

        plan.ReRunNodeIds.ShouldBe(new[] { "term" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
    }

    [Fact]
    public void Root_as_fromNode_keeps_nothing_degenerating_to_a_full_replay()
    {
        var plan = RerunFromNodePlanner.Plan(Def([N("A"), N("B"), N("C")], E("A", "B"), E("B", "C")), "A");

        plan.ReRunNodeIds.ShouldBe(new[] { "A", "B", "C" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBeEmpty("rerun-from-root re-runs the whole graph — equivalent to a plain replay");
    }

    [Fact]
    public void Unknown_fromNode_is_rejected()
    {
        Should.Throw<RerunTargetNotFoundException>(() =>
            RerunFromNodePlanner.Plan(Def([N("A"), N("B")], E("A", "B")), "ghost"));
    }

    [Fact]
    public void Container_internal_fromNode_is_rejected_with_the_container_named()
    {
        var ex = Should.Throw<RerunTargetNotFoundException>(() =>
            RerunFromNodePlanner.Plan(Def([N("A"), N("map"), N("b1", parent: "map")], E("A", "map")), "b1"));

        ex.Message.ShouldContain("map", customMessage: "the rejection names the container to rerun instead");
    }

    [Fact]
    public void A_defensive_cycle_terminates()
    {
        // The validator forbids cycles, but the closure must be self-protecting regardless.
        var plan = RerunFromNodePlanner.Plan(Def([N("A"), N("B")], E("A", "B"), E("B", "A")), "A");

        plan.ReRunNodeIds.ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
    }

    [Fact]
    public void A_disconnected_settled_island_is_kept_wholesale()
    {
        // A→B (rerun from A); a disconnected island X→Y not reachable from A → both KEPT (reused).
        var plan = RerunFromNodePlanner.Plan(Def([N("A"), N("B"), N("X"), N("Y")], E("A", "B"), E("X", "Y")), "A");

        plan.ReRunNodeIds.ShouldBe(new[] { "A", "B" }, ignoreOrder: true);
        plan.KeptNodeIds.ShouldBe(new[] { "X", "Y" }, ignoreOrder: true);
    }

    // ─── builders ────────────────────────────────────────────────────────────────
    private static NodeDefinition N(string id, string? parent = null) => new() { Id = id, TypeKey = "x", ParentId = parent };
    private static EdgeDefinition E(string from, string to, string? handle = null) => new() { From = from, To = to, SourceHandle = handle };
    private static WorkflowDefinition Def(NodeDefinition[] nodes, params EdgeDefinition[] edges) =>
        new() { SchemaVersion = 1, Nodes = nodes, Edges = edges };
}
