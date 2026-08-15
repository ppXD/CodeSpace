using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows.ModelCalls;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

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
