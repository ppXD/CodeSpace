using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;

namespace CodeSpace.UnitTests.Tasks.Phases;

/// <summary>Builders for the run-detail nouns the phase node source reads — keeps the test bodies focused on the assertions.</summary>
internal static class RunDetailFixtures
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    public static WorkflowRunDetail Run(WorkflowRunStatus status, params WorkflowRunNodeSummary[] nodes) => new()
    {
        Id = Guid.NewGuid(),
        RunNumber = 1,
        SourceType = "test",
        NormalizedPayload = EmptyObject,
        Status = status,
        CreatedDate = DateTimeOffset.UtcNow,
        Nodes = nodes,
        Outputs = EmptyObject,
    };

    public static WorkflowRunNodeSummary TopLevelNode(string nodeId, NodeStatus status, string? agentRunId = null, JsonElement? outputs = null, DateTimeOffset? startedAt = null) => new()
    {
        NodeId = nodeId,
        IterationKey = "",
        ContainerKind = null,
        Status = status,
        Inputs = EmptyObject,
        Outputs = outputs ?? EmptyObject,
        StartedAt = startedAt,
        AgentRunId = agentRunId,
        RerunnableFromHere = false,
    };

    public static WorkflowRunNodeSummary MapBranch(string mapNodeId, int index, string bodyNodeId, NodeStatus status, string agentRunId) => new()
    {
        NodeId = bodyNodeId,
        IterationKey = $"{mapNodeId}#{index}",
        ContainerKind = "flow.map",
        Status = status,
        Inputs = EmptyObject,
        Outputs = EmptyObject,
        AgentRunId = agentRunId,
        RerunnableFromHere = false,
    };

    /// <summary>A grandchild branch row of a NESTED map: its iteration key is the engine's composed "&lt;outerKey&gt;/&lt;innerSegment&gt;" shape (e.g. "outerMap#0/innerMap#0"). The outer fan-out must NOT fold this — only its DIRECT elements.</summary>
    public static WorkflowRunNodeSummary NestedMapBranch(string composedIterationKey, string bodyNodeId, NodeStatus status, string agentRunId) => new()
    {
        NodeId = bodyNodeId,
        IterationKey = composedIterationKey,
        ContainerKind = "flow.map",
        Status = status,
        Inputs = EmptyObject,
        Outputs = EmptyObject,
        AgentRunId = agentRunId,
        RerunnableFromHere = false,
    };

    /// <summary>A map node's reduced output bag. <paramref name="resultsCoverageJson"/> adds the reduce-input coverage a budget-declaring map records; omitting it is the bag every other map writes.</summary>
    public static JsonElement MapOutputs(int count, int failed, string? resultsCoverageJson = null) =>
        JsonDocument.Parse($"{{\"count\":{count},\"failed\":{failed}{(resultsCoverageJson is null ? "" : $",\"resultsCoverage\":{resultsCoverageJson}")}}}").RootElement;
}
