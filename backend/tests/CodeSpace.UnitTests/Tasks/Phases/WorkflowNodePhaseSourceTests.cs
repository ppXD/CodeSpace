using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>
/// The structural node source's pure projection (node summaries + the already-resolved ground-truth agent statuses →
/// phases). The team-scoped DB read of the AgentRun statuses is integration-tested; here we pin the per-node shape: a
/// flow.map node + its DIRECT branch rows roll into ONE 'Fan out' phase whose Agents carry the REAL AgentRunStatus
/// (never the NodeStatus name), a plain agent.code node surfaces as a one-agent 'agent' phase, a plain node is
/// agentless, a branch row is never a top-level phase, and a NESTED map's grandchild branches are NOT folded into the
/// outer fan-out.
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowNodePhaseSourceTests
{
    [Fact]
    public void Rolls_a_map_node_and_its_branches_into_one_fan_out_phase()
    {
        var branch0Agent = Guid.NewGuid();
        var branch1Agent = Guid.NewGuid();

        var nodes = new[]
        {
            RunDetailFixtures.TopLevelNode("map", NodeStatus.Success, outputs: RunDetailFixtures.MapOutputs(count: 2, failed: 0), startedAt: DateTimeOffset.UtcNow),
            RunDetailFixtures.MapBranch("map", 0, "agent", NodeStatus.Success, branch0Agent.ToString()),
            RunDetailFixtures.MapBranch("map", 1, "agent", NodeStatus.Success, branch1Agent.ToString()),
        };

        // Ground truth read team-scoped: both branch agents finished Succeeded (the AgentRunStatus vocabulary,
        // NOT the NodeStatus "Success").
        var statuses = new Dictionary<Guid, AgentRunStatus>
        {
            [branch0Agent] = AgentRunStatus.Succeeded,
            [branch1Agent] = AgentRunStatus.Succeeded,
        };

        var map = WorkflowNodePhaseSource.ProjectNodes(nodes, statuses).ShouldHaveSingleItem();
        map.Kind.ShouldBe("map");
        map.Label.ShouldBe("Fan out");
        map.Status.ShouldBe(PhaseStatus.Succeeded);
        map.SourceKey.ShouldBe(WorkflowNodePhaseSource.Key);

        map.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { branch0Agent, branch1Agent });
        map.Agents.ShouldAllBe(a => a.Status == nameof(AgentRunStatus.Succeeded), "the ref carries the REAL AgentRunStatus, not the NodeStatus name");
        map.Agents.Select(a => a.IterationKey).ShouldBe(new[] { "map#0", "map#1" });

        map.Metrics.AgentCount.ShouldBe(2);
        map.Metrics.SucceededCount.ShouldBe(2);
        map.Metrics.FailedCount.ShouldBe(0);
    }

    [Fact]
    public void A_plain_agent_node_becomes_a_one_agent_phase_carrying_the_real_agent_status()
    {
        var agentRunId = Guid.NewGuid();

        var nodes = new[] { RunDetailFixtures.TopLevelNode("agent", NodeStatus.Running, agentRunId: agentRunId.ToString()) };

        // The node row reads NodeStatus.Running, but the REAL agent run is already Succeeded — the ref must carry the
        // ground-truth AgentRunStatus, proving it does NOT echo the node status name.
        var statuses = new Dictionary<Guid, AgentRunStatus> { [agentRunId] = AgentRunStatus.Succeeded };

        var phase = WorkflowNodePhaseSource.ProjectNodes(nodes, statuses).ShouldHaveSingleItem();
        phase.Kind.ShouldBe("agent");
        phase.Status.ShouldBe(PhaseStatus.Active, "the phase status still derives from the node's own status");

        var agent = phase.Agents.ShouldHaveSingleItem();
        agent.AgentRunId.ShouldBe(agentRunId);
        agent.NodeId.ShouldBe("agent");
        agent.IterationKey.ShouldBeNull("a top-level agent node has no iteration key");
        agent.Status.ShouldBe(nameof(AgentRunStatus.Succeeded), "the ref is the REAL AgentRunStatus, not the NodeStatus name");
    }

    [Fact]
    public void An_agent_node_with_a_missing_agent_row_falls_back_to_the_node_status_name()
    {
        var agentRunId = Guid.NewGuid();

        var nodes = new[] { RunDetailFixtures.TopLevelNode("agent", NodeStatus.Running, agentRunId: agentRunId.ToString()) };

        // The agent row isn't in the team-scoped status map (team-foreign or not yet created) — the documented
        // fallback stamps the owning node's status name so the ref is never blank.
        var phase = WorkflowNodePhaseSource.ProjectNodes(nodes, EmptyStatuses).ShouldHaveSingleItem();

        phase.Agents.ShouldHaveSingleItem().Status.ShouldBe(nameof(NodeStatus.Running), "absent agent row → fall back to the node status name");
    }

    [Fact]
    public void A_plain_non_agent_node_becomes_an_agentless_node_phase()
    {
        var nodes = new[] { RunDetailFixtures.TopLevelNode("start", NodeStatus.Success) };

        var phase = WorkflowNodePhaseSource.ProjectNodes(nodes, EmptyStatuses).ShouldHaveSingleItem();

        phase.Kind.ShouldBe("node");
        phase.Agents.ShouldBeEmpty();
    }

    [Fact]
    public void Branch_rows_are_not_emitted_as_their_own_top_level_phases()
    {
        var branchAgent = Guid.NewGuid();

        var nodes = new[]
        {
            RunDetailFixtures.TopLevelNode("map", NodeStatus.Success, outputs: RunDetailFixtures.MapOutputs(1, 0)),
            RunDetailFixtures.MapBranch("map", 0, "agent", NodeStatus.Success, branchAgent.ToString()),
        };

        var phases = WorkflowNodePhaseSource.ProjectNodes(nodes, new Dictionary<Guid, AgentRunStatus> { [branchAgent] = AgentRunStatus.Succeeded });

        phases.Count.ShouldBe(1, "only the top-level map node is a phase; its branch is folded into the fan-out, not a separate row");
    }

    [Fact]
    public void A_nested_map_folds_only_its_direct_branches_not_the_grandchildren()
    {
        // An OUTER map node, its single DIRECT element-branch (which is itself an inner-map container row keyed
        // "outerMap#0"), and the inner map's GRANDCHILD branch keyed "outerMap#0/innerMap#0" (the engine composes
        // nested keys as "<outerKey>/<segment>"). The outer 'Fan out' phase must fold ONLY the direct branch — the
        // grandchild belongs to the inner map, and a StartsWith("outerMap#") match would wrongly capture it too.
        var directAgent = Guid.NewGuid();
        var grandchildAgent = Guid.NewGuid();

        var nodes = new[]
        {
            RunDetailFixtures.TopLevelNode("outerMap", NodeStatus.Success, outputs: RunDetailFixtures.MapOutputs(count: 1, failed: 0), startedAt: DateTimeOffset.UtcNow),
            RunDetailFixtures.MapBranch("outerMap", 0, "innerMap", NodeStatus.Success, directAgent.ToString()),
            RunDetailFixtures.NestedMapBranch("outerMap#0/innerMap#0", "agent", NodeStatus.Success, grandchildAgent.ToString()),
        };

        var statuses = new Dictionary<Guid, AgentRunStatus>
        {
            [directAgent] = AgentRunStatus.Succeeded,
            [grandchildAgent] = AgentRunStatus.Succeeded,
        };

        var map = WorkflowNodePhaseSource.ProjectNodes(nodes, statuses).ShouldHaveSingleItem("only the outer map is a top-level phase");

        map.Id.ShouldBe("outerMap");
        map.Agents.Select(a => a.AgentRunId).ShouldBe(new[] { directAgent }, "the outer fan-out folds ONLY its direct element branch — the grandchild is the inner map's, not the outer's");
        map.Agents.ShouldNotContain(a => a.AgentRunId == grandchildAgent, "the nested grandchild must NOT be folded into the outer phase");
        map.Metrics.AgentCount.ShouldBe(1, "the agent count matches the outer map's direct-element count, not the grandchildren");
    }

    private static readonly IReadOnlyDictionary<Guid, AgentRunStatus> EmptyStatuses = new Dictionary<Guid, AgentRunStatus>();
}
