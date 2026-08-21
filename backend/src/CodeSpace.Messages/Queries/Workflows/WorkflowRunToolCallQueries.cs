using System.ComponentModel.DataAnnotations;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows.ToolCalls;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

public sealed record ListWorkflowRunToolCallsQuery : IQuery<WorkflowRunToolCallPage?>, IRequireTeamMembership, IValidatableObject
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    public required Guid RunId { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = DefaultPageSize;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RunId == Guid.Empty) yield return new ValidationResult("RunId must be non-empty.", [nameof(RunId)]);
        if (Limit is < 1 or > MaximumPageSize)
            yield return new ValidationResult($"Limit must be between 1 and {MaximumPageSize}.", [nameof(Limit)]);
        if (Cursor is not null && !WorkflowRunToolCallPageCursor.TryDecode(Cursor, out _))
            yield return new ValidationResult("Cursor must be an opaque Workflow Run tool-call page cursor.", [nameof(Cursor)]);
    }
}

public sealed record GetWorkflowRunToolCallQuery : IQuery<WorkflowRunToolCallDetail?>, IRequireTeamMembership
{
    public required Guid RunId { get; init; }
    public required Guid ToolCallId { get; init; }
}
