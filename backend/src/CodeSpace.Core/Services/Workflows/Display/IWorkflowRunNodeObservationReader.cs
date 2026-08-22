using System.Text.Json;
using CodeSpace.Messages.Dtos.Workflows;

namespace CodeSpace.Core.Services.Workflows.Display;

public sealed record WorkflowRunNodeObservationRequest(Guid RunId, Guid TeamId, WorkflowRunViewScope Scope);

/// <summary>Batch, bounded display leaves required by structural node/agent/map phase projections.</summary>
public interface IWorkflowRunNodeObservationReader
{
    Task<WorkflowRunNodeObservation?> ReadAsync(WorkflowRunNodeObservationRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowRunNodeObservation
{
    public required WorkflowRunViewMetadata Metadata { get; init; }
    public required WorkflowRunViewAvailability Availability { get; init; }
    public required IReadOnlyDictionary<string, WorkflowRunNodeLeafObservation> TopLevelLeaves { get; init; }
}

public enum WorkflowRunNodeLeafState
{
    Missing,
    Exact,
    Truncated,
    Invalid,
}

public sealed record WorkflowRunNodeLeafObservation
{
    public required WorkflowRunNodeLeafState ErrorState { get; init; }
    public string? ErrorPrefix { get; init; }
    public WorkflowRunMapMetricsObservation? MapMetrics { get; init; }
}

public sealed record WorkflowRunMapMetricsObservation
{
    public required int Count { get; init; }
    public required int Failed { get; init; }
    public required WorkflowRunNodeLeafState ResultsCoverageState { get; init; }
    public JsonElement? ResultsCoverage { get; init; }
}
