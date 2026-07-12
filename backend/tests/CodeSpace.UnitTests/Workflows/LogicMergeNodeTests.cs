using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: <c>logic.merge</c> joins only its DIRECT graph predecessors (context.IncomingNodeIds), not every node
/// that happened to complete earlier in the run. Regression for P0.5: the node used to read the run-wide
/// <c>scope.Nodes</c>, so a later-completing UNRELATED node (no edge into the merge) could win first-non-empty or
/// pollute the all-barrier's merged object.
/// </summary>
[Trait("Category", "Unit")]
public class LogicMergeNodeTests
{
    private static IReadOnlyDictionary<string, JsonElement> Outputs(object o) =>
        JsonSerializer.SerializeToElement(o).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    /// <summary>Build a merge context: scope.Nodes seeded in completion order, plus the merge's direct predecessors.</summary>
    private static NodeRunContext BuildContext(string strategy, IReadOnlyList<string> incoming, params (string Id, IReadOnlyDictionary<string, JsonElement> Out)[] completed)
    {
        var scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>() };
        foreach (var (id, outputs) in completed)
            scope.Nodes[id] = outputs;   // Dictionary preserves insertion order = completion order (as the engine relies on)

        return new NodeRunContext
        {
            Inputs = new Dictionary<string, JsonElement>(),
            Config = new Dictionary<string, JsonElement> { ["strategy"] = JsonSerializer.SerializeToElement(strategy) },
            RawInputs = JsonDocument.Parse("{}").RootElement,
            RawConfig = JsonDocument.Parse("{}").RootElement,
            Scope = scope,
            Logger = NullLogger.Instance,
            Observability = NodeObservability.NoOp,
            NodeId = "merge",
            IncomingNodeIds = incoming,
        };
    }

    [Fact]
    public async Task First_non_empty_picks_the_last_predecessor_ignoring_a_later_unrelated_node()
    {
        // Completion order: predA, predB, THEN an unrelated node that finishes last. The unrelated node is NOT an
        // edge-into-merge predecessor. Old (buggy) behaviour reversed scope.Nodes and returned `unrelated`; the fix
        // filters to {predA, predB} first, so first-non-empty returns predB (the last predecessor that fired).
        var ctx = BuildContext(
            "first-non-empty",
            incoming: new[] { "predA", "predB" },
            ("predA", Outputs(new { from = "A" })),
            ("predB", Outputs(new { from = "B" })),
            ("unrelated", Outputs(new { from = "X" })));

        var result = await new LogicMergeNode().RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs["from"].GetString().ShouldBe("B");   // predB, not the later `unrelated` (X)
    }

    [Fact]
    public async Task First_non_empty_skips_an_empty_predecessor_to_the_prior_non_empty_one()
    {
        var ctx = BuildContext(
            "first-non-empty",
            incoming: new[] { "predA", "predB" },
            ("predA", Outputs(new { from = "A" })),
            ("predB", new Dictionary<string, JsonElement>()));   // predB ran but produced nothing

        var result = await new LogicMergeNode().RunAsync(ctx, CancellationToken.None);

        result.Outputs["from"].GetString().ShouldBe("A");
    }

    [Fact]
    public async Task All_barrier_keys_only_direct_predecessors_excluding_unrelated_nodes()
    {
        var ctx = BuildContext(
            "all",
            incoming: new[] { "predA", "predB" },
            ("predA", Outputs(new { v = 1 })),
            ("unrelated", Outputs(new { v = 99 })),
            ("predB", Outputs(new { v = 2 })));

        var result = await new LogicMergeNode().RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs.Keys.OrderBy(k => k).ShouldBe(new[] { "predA", "predB" });   // `unrelated` is not joined
    }

    [Fact]
    public async Task Emits_empty_when_no_completed_node_is_a_direct_predecessor()
    {
        // A node completed, but it doesn't feed the merge (empty predecessor set) — the merge reaches for nothing.
        var ctx = BuildContext(
            "first-non-empty",
            incoming: Array.Empty<string>(),
            ("unrelated", Outputs(new { from = "X" })));

        var result = await new LogicMergeNode().RunAsync(ctx, CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success);
        result.Outputs.ShouldBeEmpty();
    }
}
