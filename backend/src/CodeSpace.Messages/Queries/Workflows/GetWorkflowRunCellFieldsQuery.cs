using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>Read one bounded, body-blind field-descriptor page for an exact cell coordinate returned by the run view.</summary>
public sealed record GetWorkflowRunCellFieldsQuery : IQuery<WorkflowRunCellFieldPage?>, IRequireTeamMembership
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 100;

    public required Guid RunId { get; init; }
    public WorkflowRunViewScope Scope { get; init; } = WorkflowRunViewScope.LineageMerged;
    public required Guid SourceRunId { get; init; }
    public required string NodeId { get; init; }
    /// <summary>Null/empty is the top-level cell key; nullable so ASP.NET query binding does not reject <c>?iterationKey=</c>.</summary>
    public string? IterationKey { get; init; }
    public string? Cursor { get; init; }
    public int Limit { get; init; } = DefaultPageSize;
}
