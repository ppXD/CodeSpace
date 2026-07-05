using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Timeline;

/// <summary>
/// The flow.map orchestration-beat maps (planner + dispatch): the beat names its count, an empty fan-out / plan says
/// so instead of a bland "0", and a FAILED planner / fan-out node reads Error with its node error — so a non-supervisor
/// run's plan-time / dispatch-time failure is as legible as a supervisor's. No DB.
/// </summary>
[Trait("Category", "Unit")]
public class MapBeatTimelineMapTests
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    private static WorkflowRunNodeSummary Node(NodeStatus status = NodeStatus.Success, string? error = null) => new()
    {
        NodeId = "map-1",
        IterationKey = "",
        Status = status,
        Error = error,
        Inputs = Empty,
        Outputs = Empty,
        RerunnableFromHere = false,
    };

    // ── Dispatch beat ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "Dispatched no agents")]
    [InlineData(1, "Dispatched 1 agent")]
    [InlineData(3, "Dispatched 3 agents")]
    public void Dispatch_title_counts_the_agents_and_names_an_empty_fan_out(int agentCount, string expected)
    {
        MapDispatchTimelineMap.ToEvent(Node(), agentCount, At).Title.ShouldBe(expected);
    }

    [Fact]
    public void Dispatch_of_no_agent_explains_it_dispatched_nothing()
    {
        MapDispatchTimelineMap.ToEvent(Node(), 0, At).Summary.ShouldBe("No agent was dispatched — this map fanned out no branch.");
    }

    [Theory]
    [InlineData(NodeStatus.Success, TimelineSeverity.Info)]
    [InlineData(NodeStatus.Running, TimelineSeverity.Info)]
    [InlineData(NodeStatus.Failure, TimelineSeverity.Error)]
    public void Dispatch_tone_rides_the_map_node_status(NodeStatus status, TimelineSeverity expected)
    {
        MapDispatchTimelineMap.ToEvent(Node(status), 2, At).Severity.ShouldBe(expected);
    }

    [Fact]
    public void A_failed_fan_out_carries_the_node_error()
    {
        MapDispatchTimelineMap.ToEvent(Node(NodeStatus.Failure, error: "the map handler threw"), 2, At).Summary.ShouldBe("the map handler threw");
    }

    // ── Plan beat ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "Planned no subtasks")]
    [InlineData(1, "Planned 1 subtask")]
    [InlineData(5, "Planned 5 subtasks")]
    public void Plan_title_counts_the_subtasks_and_names_an_empty_plan(int subtaskCount, string expected)
    {
        MapPlannerTimelineMap.ToEvent(Node(), subtaskCount, At).Title.ShouldBe(expected);
    }

    [Fact]
    public void A_failed_planner_reads_planning_failed_with_its_error_at_error_tone()
    {
        var ev = MapPlannerTimelineMap.ToEvent(Node(NodeStatus.Failure, error: "the planner model timed out"), 0, At);

        ev.Title.ShouldBe("Planning failed", "a planner that failed is not a bland 'Planned 0 subtasks'");
        ev.Summary.ShouldBe("the planner model timed out");
        ev.Severity.ShouldBe(TimelineSeverity.Error);
    }

    [Fact]
    public void A_succeeded_planner_is_a_neutral_info_beat()
    {
        MapPlannerTimelineMap.ToEvent(Node(NodeStatus.Success), 3, At).Severity.ShouldBe(TimelineSeverity.Info);
    }
}
