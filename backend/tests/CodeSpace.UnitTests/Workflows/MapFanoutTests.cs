using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the shared flow.map fan-out resolution — the ONE place "which top-level node is a map, and which rows are its
/// DIRECT element branches" is decided (shared by the phase board + the journal's map-dispatch beat). Pins that a map is a
/// top-level node with direct <c>flow.map</c> branches, that a nested grandchild is excluded, and that a non-map run
/// contributes nothing. Pure over the node summaries — no database.
/// </summary>
[Trait("Category", "Unit")]
public class MapFanoutTests
{
    [Fact]
    public void Resolves_a_map_node_and_its_direct_branches_excluding_grandchildren()
    {
        // A map container "map" fans "agent" out over 2 elements (iteration keys "map#0"/"map#1"); a grandchild key
        // "map#0/inner" belongs to a nested container, NOT this map's direct fan-out.
        var nodes = new[]
        {
            Node("planner"),                                              // top-level, no branches → not a map
            Node("map"),                                                  // the map container node (top-level)
            Node("agent", "map#0", MapFanout.ContainerKind, "a1"),       // its 2 direct element branches
            Node("agent", "map#1", MapFanout.ContainerKind, "a2"),
            Node("inner", "map#0/inner", MapFanout.ContainerKind),       // a grandchild — excluded
        };

        var maps = MapFanout.MapNodesOf(nodes);

        maps.Count.ShouldBe(1, "only the node with DIRECT flow.map branches is a map");
        maps[0].Node.NodeId.ShouldBe("map");
        maps[0].Branches.Select(b => b.IterationKey).ShouldBe(new[] { "map#0", "map#1" }, "the two direct element branches, ordered — the grandchild is excluded");
        maps[0].Branches.Select(b => b.AgentRunId).ShouldBe(new[] { "a1", "a2" });
    }

    [Fact]
    public void A_run_with_no_map_contributes_nothing()
    {
        MapFanout.MapNodesOf(new[] { Node("plan"), Node("agent", agentRunId: "x") })
            .ShouldBeEmpty("a plain / agent workflow with no fan-out has no map dispatch");
    }

    private static WorkflowRunNodeSummary Node(string nodeId, string iterationKey = "", string? containerKind = null, string? agentRunId = null) =>
        new()
        {
            NodeId = nodeId, IterationKey = iterationKey, ContainerKind = containerKind, AgentRunId = agentRunId,
            Status = NodeStatus.Success, Inputs = default, Outputs = default, RerunnableFromHere = false,
        };
}
