using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the two grep-anchor formatters the dep-handoff diagnosis joins on — the plan log's dependency-edge
/// token and the staging log's unit list. These strings are READ BY HUMANS AND GREPS over CI logs (the
/// run-31170757534 investigation could not answer "does the model spawn the edge-bearing unit?" because neither
/// side was named); pinning the exact shape keeps the join stable across refactors.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorStagingLogTests
{
    [Fact]
    public void A_flat_plan_reads_none()
    {
        RealSupervisorActionExecutor.DescribeEdges(new[] { Subtask("s1"), Subtask("s2") }).ShouldBe("(none)");
    }

    [Fact]
    public void Edge_bearing_units_read_one_token_each_in_plan_order()
    {
        var subtasks = new[]
        {
            Subtask("s1"),
            Subtask("s2", dependsOn: new[] { "s1" }),
            Subtask("s3", dependsOn: new[] { "s1", "s2" }),
        };

        RealSupervisorActionExecutor.DescribeEdges(subtasks).ShouldBe("s2->[s1] s3->[s1,s2]");
    }

    [Fact]
    public void Staged_units_join_their_plan_local_ids()
    {
        var tasks = new (AgentTask Task, SupervisorAgentDispatch? Spec)[]
        {
            (Task("s1"), null),
            (Task("s2"), null),
        };

        RealSupervisorActionExecutor.DescribeStagedUnits(tasks).ShouldBe("s1,s2");
    }

    [Fact]
    public void A_free_form_spawn_with_no_subtask_key_reads_unkeyed()
    {
        RealSupervisorActionExecutor.DescribeStagedUnits(new (AgentTask Task, SupervisorAgentDispatch? Spec)[] { (Task(null), null) }).ShouldBe("(unkeyed)");
    }

    private static SupervisorPlannedSubtask Subtask(string id, string[]? dependsOn = null) =>
        new() { Id = id, Title = id.ToUpperInvariant(), Instruction = $"do {id}", DependsOn = dependsOn };

    private static AgentTask Task(string? subtaskId) => new() { Goal = "g", Harness = "scripted", SubtaskId = subtaskId };
}
