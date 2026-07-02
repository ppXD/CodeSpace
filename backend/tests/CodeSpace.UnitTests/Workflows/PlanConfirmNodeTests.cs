using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The plan.confirm node's DURABLE wire literals + manifest contract. The payload kind, iteration-key format,
/// and revision origin-key prefix all live in persisted rows (wait payloads, wait iteration keys, work_plan
/// origin keys) — a reword strands every parked run and breaks crash-replay adoption of pre-rename rows, so
/// each is a pinned, visible decision (the same stance as the supervisor gate's marker pins).
/// </summary>
public class PlanConfirmNodeTests
{
    [Fact]
    public void The_wait_payload_kind_is_pinned() =>
        PlanConfirmNode.WaitPayloadKind.ShouldBe("plan-confirm");

    [Fact]
    public void The_revision_origin_key_prefix_is_pinned() =>
        PlanConfirmNode.RevisionKeyPrefix.ShouldBe("plan-confirm#rev-of-v");

    [Fact]
    public void The_iteration_key_is_versioned_and_pinned()
    {
        PlanConfirmNode.IterationKeyFor(1).ShouldBe("plan-confirm#v1");
        PlanConfirmNode.IterationKeyFor(7).ShouldBe("plan-confirm#v7");
    }

    [Fact]
    public void Manifest_pins_the_node_contract()
    {
        var node = new PlanConfirmNode(null!);

        node.TypeKey.ShouldBe("plan.confirm");
        node.Manifest.CanSuspend.ShouldBeTrue("the gate parks per plan version");
        node.Manifest.IsSideEffecting.ShouldBeTrue("a revision is a billed planner call");

        ConfigKeys(node).ShouldBe(new[] { "plannerModelId", "reviewMode", "reviewerModelId", "flatPlan", "maxRevisions" }, ignoreOrder: true);
        InputKeys(node).ShouldBe(new[] { "goal", "grounding" }, ignoreOrder: true);
        OutputKeys(node).ShouldBe(new[] { "planId", "version", "approved", "goal", "items", "json" }, ignoreOrder: true);

        node.Manifest.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "goal" }, "only the goal is required — grounding is an optional bind");
    }

    [Fact]
    public void The_default_revision_cap_is_pinned() =>
        PlanConfirmNode.DefaultMaxRevisions.ShouldBe(5);

    private static IReadOnlyList<string> ConfigKeys(PlanConfirmNode node) =>
        node.Manifest.ConfigSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

    private static IReadOnlyList<string> InputKeys(PlanConfirmNode node) =>
        node.Manifest.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

    private static IReadOnlyList<string> OutputKeys(PlanConfirmNode node) =>
        node.Manifest.OutputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
}
