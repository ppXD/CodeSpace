using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Display;

public sealed record WorkflowMapPlanObservationRequest(Guid RunId, Guid TeamId, WorkflowRunViewScope Scope);

/// <summary>Bounded, observation-only access to the producer leaves that author flow.map branch plans.</summary>
public interface IWorkflowMapPlanObservationReader
{
    Task<WorkflowMapPlanObservation?> ReadAsync(WorkflowMapPlanObservationRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowMapPlanObservation
{
    public required Guid RunId { get; init; }
    public required WorkflowRunViewAvailability Availability { get; init; }
    public required DateTimeOffset AnchorAt { get; init; }
    public required IReadOnlyList<WorkflowMapPlannerObservation> Planners { get; init; }
}

public enum WorkflowMapPlanLeafState
{
    Missing,
    Exact,
    Truncated,
    Invalid,
    Unavailable,
}

public sealed record WorkflowMapPlannerObservation
{
    public required string ProducerNodeId { get; init; }
    public required NodeStatus Status { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required Guid StateRecordId { get; init; }
    public required long StateRecordSequence { get; init; }
    public required WorkflowMapPlanLeafState ErrorState { get; init; }
    public string? ErrorPrefix { get; init; }
    public required WorkflowMapPlanLeafState SubtasksState { get; init; }
    public required int SubtasksTotalCount { get; init; }
    public JsonElement? Subtasks { get; init; }
    public required WorkflowMapPlanLeafState ModelUsageState { get; init; }
    public JsonElement? ModelOutputs { get; init; }
}
