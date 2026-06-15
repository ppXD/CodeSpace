using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>
/// The two total status mappings into the lone closed enum <see cref="PhaseStatus"/> — the only place a NodeStatus /
/// SupervisorDecisionStatus crosses into the UI render vocabulary. Every enum value is pinned so a new substrate
/// value forces a compile/test-visible decision.
/// </summary>
[Trait("Category", "Unit")]
public class PhaseStatusMapTests
{
    [Theory]
    [InlineData(NodeStatus.Pending, PhaseStatus.Pending)]
    [InlineData(NodeStatus.Running, PhaseStatus.Active)]
    [InlineData(NodeStatus.Suspended, PhaseStatus.Waiting)]
    [InlineData(NodeStatus.Success, PhaseStatus.Succeeded)]
    [InlineData(NodeStatus.Failure, PhaseStatus.Failed)]
    [InlineData(NodeStatus.Skipped, PhaseStatus.Skipped)]
    public void Maps_every_node_status(NodeStatus node, PhaseStatus expected) =>
        PhaseStatusMap.FromNode(node).ShouldBe(expected);

    [Theory]
    [InlineData(SupervisorDecisionStatus.Pending, PhaseStatus.Pending)]
    [InlineData(SupervisorDecisionStatus.AwaitingApproval, PhaseStatus.Waiting)]
    [InlineData(SupervisorDecisionStatus.Running, PhaseStatus.Active)]
    [InlineData(SupervisorDecisionStatus.Succeeded, PhaseStatus.Succeeded)]
    [InlineData(SupervisorDecisionStatus.Failed, PhaseStatus.Failed)]
    [InlineData(SupervisorDecisionStatus.Expired, PhaseStatus.Failed)]
    public void Maps_every_decision_status(SupervisorDecisionStatus decision, PhaseStatus expected) =>
        PhaseStatusMap.FromDecision(decision).ShouldBe(expected);

    /// <summary>The test owns the INTENDED node→phase map; a new NodeStatus value that isn't added here fails the cover-check below — forcing an explicit decision rather than silently hitting the <c>_ =&gt; Pending</c> fallback.</summary>
    private static readonly IReadOnlyDictionary<NodeStatus, PhaseStatus> ExpectedNodeMap = new Dictionary<NodeStatus, PhaseStatus>
    {
        [NodeStatus.Pending] = PhaseStatus.Pending,
        [NodeStatus.Running] = PhaseStatus.Active,
        [NodeStatus.Suspended] = PhaseStatus.Waiting,
        [NodeStatus.Success] = PhaseStatus.Succeeded,
        [NodeStatus.Failure] = PhaseStatus.Failed,
        [NodeStatus.Skipped] = PhaseStatus.Skipped,
    };

    private static readonly IReadOnlyDictionary<SupervisorDecisionStatus, PhaseStatus> ExpectedDecisionMap = new Dictionary<SupervisorDecisionStatus, PhaseStatus>
    {
        [SupervisorDecisionStatus.Pending] = PhaseStatus.Pending,
        [SupervisorDecisionStatus.AwaitingApproval] = PhaseStatus.Waiting,
        [SupervisorDecisionStatus.Running] = PhaseStatus.Active,
        [SupervisorDecisionStatus.Succeeded] = PhaseStatus.Succeeded,
        [SupervisorDecisionStatus.Failed] = PhaseStatus.Failed,
        [SupervisorDecisionStatus.Expired] = PhaseStatus.Failed,
    };

    [Fact]
    public void Maps_every_node_status_value_to_its_intended_phase()
    {
        var values = Enum.GetValues<NodeStatus>();

        ExpectedNodeMap.Keys.ShouldBe(values, ignoreOrder: true,
            "every NodeStatus value must have an INTENDED phase in this test's map — a new value forces an explicit decision here, not a silent _ => Pending fallback");

        foreach (var value in values)
            PhaseStatusMap.FromNode(value).ShouldBe(ExpectedNodeMap[value], $"NodeStatus.{value} must map to its intended phase, not the fallback");
    }

    [Fact]
    public void Maps_every_decision_status_value_to_its_intended_phase()
    {
        var values = Enum.GetValues<SupervisorDecisionStatus>();

        ExpectedDecisionMap.Keys.ShouldBe(values, ignoreOrder: true,
            "every SupervisorDecisionStatus value must have an INTENDED phase in this test's map — a new value forces an explicit decision here, not a silent _ => Pending fallback");

        foreach (var value in values)
            PhaseStatusMap.FromDecision(value).ShouldBe(ExpectedDecisionMap[value], $"SupervisorDecisionStatus.{value} must map to its intended phase, not the fallback");
    }
}
