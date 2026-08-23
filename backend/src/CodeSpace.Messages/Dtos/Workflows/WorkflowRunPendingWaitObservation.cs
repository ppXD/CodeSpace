using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Workflows;

public sealed record WorkflowRunPendingWaitObservation
{
    public required Guid RunId { get; init; }
    public WorkflowRunPendingWait? Wait { get; init; }
}

public sealed record WorkflowRunPendingWait
{
    public required Guid Id { get; init; }
    public required string NodeId { get; init; }
    public required string Kind { get; init; }
    public required string Token { get; init; }
    public DateTimeOffset? WakeAt { get; init; }
    public required WorkflowRunPendingWaitPromptState PromptState { get; init; }
    public string? PromptPrefix { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunPendingWaitPromptState
{
    Missing,
    Exact,
    Truncated,
    Invalid,
}
