using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>The bounded canonical identity needed to route one team-owned Workflow Run; no execution graph or payload bytes.</summary>
public sealed record WorkflowRunIdentity
{
    public required Guid Id { get; init; }
    public required long RunNumber { get; init; }
    public required WorkflowRunStatus Status { get; init; }
}
