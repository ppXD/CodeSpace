using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the shared flow.map PLANNER resolution — the ONE place "which node authored the subtasks this map fans out,
/// and what were they" is decided (shared by the map-plan timeline source + its plan facts source). Pins that the producer
/// is resolved from the map's OWN <c>items</c> binding (<c>{{nodes.&lt;id&gt;.outputs.json.subtasks}}</c>) — builder-agnostic,
/// NOT a hardcoded "planner" — that the confirm gate is followed when the map binds the approved version, and that a
/// literal-items map / a missing definition / an empty plan contributes nothing. Pure over the run detail — no database.
/// </summary>
[Trait("Category", "Unit")]
public class MapPlanTests
{
    [Fact]
    public void Resolves_the_planner_from_the_maps_items_binding_and_reads_its_subtasks()
    {
        var run = RunWith("{{nodes.planner.outputs.json.subtasks}}", Planner("planner", Subtasks(("s1", "First"), ("s2", "Second"))));

        var planners = MapPlan.PlannersOf(run);

        planners.Count.ShouldBe(1);
        planners[0].Producer.NodeId.ShouldBe("planner", "the producer is read from the map's items binding, not a hardcoded id");
        planners[0].Subtasks.GetArrayLength().ShouldBe(2, "the two subtasks the planner authored");
    }

    [Fact]
    public void Follows_the_confirm_gate_when_the_map_binds_the_approved_version()
    {
        // Under the confirm gate the map binds the CONFIRM node's approved plan — the resolution follows the binding, so
        // the plan beat anchors on the approved version, not the pre-approval draft.
        var run = RunWith("{{nodes.confirm.outputs.json.subtasks}}", Planner("confirm", Subtasks(("s1", "Only"))));

        MapPlan.PlannersOf(run).Single().Producer.NodeId.ShouldBe("confirm");
    }

    [Fact]
    public void A_map_with_literal_items_has_no_planner()
    {
        var run = RunWith(itemsBinding: null, Planner("planner", Subtasks(("s1", "First"))));

        MapPlan.PlannersOf(run).ShouldBeEmpty("a literal-items map fans out no node's plan — nothing to surface");
    }

    [Fact]
    public void A_run_with_no_pinned_definition_has_no_planner()
    {
        var run = RunWith("{{nodes.planner.outputs.json.subtasks}}", Planner("planner", Subtasks(("s1", "First")))) with { Definition = null };

        MapPlan.PlannersOf(run).ShouldBeEmpty("with no pinned definition the items binding can't be read");
    }

    [Fact]
    public void A_planner_that_authored_no_subtasks_contributes_nothing()
    {
        var run = RunWith("{{nodes.planner.outputs.json.subtasks}}", Planner("planner", Subtasks()));

        MapPlan.PlannersOf(run).ShouldBeEmpty("an empty plan is not a plan beat");
    }

    [Fact]
    public void A_run_with_no_map_contributes_nothing()
    {
        // A plain run (no flow.map fan-out) never resolves a planner — MapPlan is scoped to map nodes.
        var run = new WorkflowRunDetail
        {
            Id = Guid.NewGuid(), SourceType = "test", NormalizedPayload = Obj("{}"), Status = WorkflowRunStatus.Success,
            CreatedDate = DateTimeOffset.UtcNow, Outputs = Obj("{}"),
            Nodes = new[] { Planner("planner", Subtasks(("s1", "First"))) },
            Definition = new WorkflowDefinition { Nodes = Array.Empty<NodeDefinition>(), Edges = Array.Empty<EdgeDefinition>() },
        };

        MapPlan.PlannersOf(run).ShouldBeEmpty();
    }

    // ── fixtures ──

    /// <summary>A run of planner → map (fanned out over 2 branches), the map binding its items per <paramref name="itemsBinding"/> (null = a literal array).</summary>
    private static WorkflowRunDetail RunWith(string? itemsBinding, WorkflowRunNodeSummary planner)
    {
        var mapInputs = itemsBinding is null ? Obj("{\"items\":[1,2]}") : Obj($"{{\"items\":\"{itemsBinding}\"}}");

        var definition = new WorkflowDefinition
        {
            Nodes = new[]
            {
                new NodeDefinition { Id = planner.NodeId, TypeKey = "plan.author" },
                new NodeDefinition { Id = "map", TypeKey = "flow.map", Inputs = mapInputs },
            },
            Edges = Array.Empty<EdgeDefinition>(),
        };

        return new WorkflowRunDetail
        {
            Id = Guid.NewGuid(), SourceType = "test", NormalizedPayload = Obj("{}"), Status = WorkflowRunStatus.Success,
            CreatedDate = DateTimeOffset.UtcNow, Outputs = Obj("{}"), Definition = definition,
            Nodes = new[] { planner, Map("map"), Branch("map", 0), Branch("map", 1) },
        };
    }

    private static WorkflowRunNodeSummary Planner(string nodeId, JsonElement outputs) => new()
    {
        NodeId = nodeId, IterationKey = "", ContainerKind = null, Status = NodeStatus.Success,
        Inputs = default, Outputs = outputs, CompletedAt = DateTimeOffset.UtcNow, RerunnableFromHere = false,
    };

    private static WorkflowRunNodeSummary Map(string nodeId) => new()
    {
        NodeId = nodeId, IterationKey = "", ContainerKind = null, Status = NodeStatus.Success,
        Inputs = default, Outputs = default, RerunnableFromHere = false,
    };

    private static WorkflowRunNodeSummary Branch(string mapNodeId, int index) => new()
    {
        NodeId = "agent", IterationKey = $"{mapNodeId}#{index}", ContainerKind = MapFanout.ContainerKind,
        Status = NodeStatus.Success, Inputs = default, Outputs = default, AgentRunId = $"a{index}", RerunnableFromHere = false,
    };

    private static JsonElement Subtasks(params (string Id, string Title)[] items)
    {
        var arr = string.Join(",", items.Select(i => $"{{\"id\":\"{i.Id}\",\"title\":\"{i.Title}\"}}"));
        return Obj($"{{\"json\":{{\"subtasks\":[{arr}]}}}}");
    }

    private static JsonElement Obj(string json) => JsonDocument.Parse(json).RootElement;
}
