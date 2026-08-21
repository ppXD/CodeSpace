using System.Text.Json.Serialization;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Bounded display metadata for one Workflow Run. It deliberately excludes run payloads, run outputs, node inputs,
/// node outputs, wait payloads and artifact references. Those bodies belong to separately scoped, bounded readers.
/// </summary>
public sealed record WorkflowRunViewMetadata
{
    public required Guid RunId { get; init; }
    public required long RunNumber { get; init; }
    public Guid? WorkflowId { get; init; }
    public int? WorkflowVersion { get; init; }
    public required string SourceType { get; init; }
    public Guid? ParentRunId { get; init; }
    public required WorkflowRunStatus Status { get; init; }
    public required bool HasError { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required WorkflowRunViewScope Scope { get; init; }
    public required WorkflowRunViewAvailability CellsAvailability { get; init; }
    public required WorkflowRunViewAvailability LinksAvailability { get; init; }
    public required IReadOnlyList<WorkflowRunCellMetadata> Cells { get; init; }
    public required WorkflowRunViewAvailability TopologyAvailability { get; init; }
    public WorkflowRunCanvasTopology? Topology { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunViewScope
{
    LineageMerged,
    AttemptOnly,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunViewAvailability
{
    Available,
    Unavailable,
    /// <summary>The prefix is trustworthy and bounded, but more matching metadata exists.</summary>
    Truncated,
    TooLarge,
    Corrupt,
}

/// <summary>
/// One bounded cell observation. <see cref="SourceRunId"/> is the attempt whose immutable ledger records own this
/// cell; together with <see cref="NodeId"/> and <see cref="IterationKey"/> it is the exact body-read coordinate.
/// </summary>
public sealed record WorkflowRunCellMetadata
{
    public required Guid SourceRunId { get; init; }
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }
    public string? ContainerKind { get; init; }
    public required NodeStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public Guid? ChildRunId { get; init; }
    public Guid? AgentRunId { get; init; }
    public required bool RerunnableFromHere { get; init; }
}

/// <summary>The narrow graph shape the read-only run canvas consumes; no node config, prompt, input or output JSON.</summary>
public sealed record WorkflowRunCanvasTopology
{
    public required IReadOnlyList<WorkflowRunCanvasNode> Nodes { get; init; }
    public required IReadOnlyList<WorkflowRunCanvasEdge> Edges { get; init; }
}

public sealed record WorkflowRunCanvasNode
{
    public required string Id { get; init; }
    public required string TypeKey { get; init; }
    public string? Label { get; init; }
    public string? ParentId { get; init; }
    public WorkflowRunCanvasPosition? Position { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
}

public sealed record WorkflowRunCanvasPosition
{
    public required double X { get; init; }
    public required double Y { get; init; }
}

public sealed record WorkflowRunCanvasEdge
{
    public required string From { get; init; }
    public required string To { get; init; }
    public string? SourceHandle { get; init; }
    public string? TargetHandle { get; init; }
    public string? Condition { get; init; }
}
