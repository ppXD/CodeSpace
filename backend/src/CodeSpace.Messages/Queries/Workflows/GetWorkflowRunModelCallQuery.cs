using System.ComponentModel.DataAnnotations;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows.ModelCalls;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

public sealed record ListWorkflowRunModelCallsQuery : IQuery<WorkflowRunModelCallPage?>, IRequireTeamMembership, IValidatableObject
{
    public const int DefaultPageSize = 100;
    public const int MaximumPageSize = 200;

    public required Guid RunId { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = DefaultPageSize;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RunId == Guid.Empty) yield return new ValidationResult("RunId must be non-empty.", [nameof(RunId)]);
        if (Limit is < 1 or > MaximumPageSize) yield return new ValidationResult($"Limit must be between 1 and {MaximumPageSize}.", [nameof(Limit)]);
        if (Cursor is not null && !WorkflowRunModelCallPageCursor.TryDecode(Cursor, out _))
            yield return new ValidationResult("Cursor must be an opaque Workflow Run model-call page cursor.", [nameof(Cursor)]);
    }
}

public sealed record GetWorkflowRunModelCallByIdQuery : IQuery<WorkflowRunModelCallDetailMetadata?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
    public Guid WorkflowRunModelCallId { get; init; }
}

public sealed record GetWorkflowRunModelCallBodyQuery : IQuery<WorkflowRunModelCallBodyPage?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
    public Guid WorkflowRunModelCallId { get; init; }
    public WorkflowRunModelCallBody Body { get; init; }
    public Guid? AttemptId { get; init; }
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = 64 * 1024;
}

public sealed record GetWorkflowRunModelCallQuery : IQuery<WorkflowRunModelCallMetadata?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
    public long Sequence { get; init; }
}

public sealed record GetWorkflowRunModelCallPartQuery : IQuery<WorkflowRunModelCallPartPage?>, IRequireTeamMembership
{
    public Guid RunId { get; init; }
    public long Sequence { get; init; }
    public WorkflowRunModelCallPart Part { get; init; }
    public long OffsetBytes { get; init; }
    public int LimitBytes { get; init; } = 64 * 1024;
}
