using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// The runs cockpit's true scoped counts (the four status cards). Team-scoped (<see cref="IRequireTeamMembership"/>);
/// the team comes from <c>ICurrentTeam</c>, never the wire. Binds the SAME scope dimensions the bar sets (the entity
/// filters) and folds them into a <see cref="RunListFilter"/> via <see cref="ToFilter"/>; the per-status counts are
/// computed by the service over that base, so this query carries no status/pagination fields. <see cref="Today"/> is
/// the caller's local start-of-day (the day boundary is the user's timezone).
/// </summary>
public sealed record GetTeamRunSummaryQuery : IQuery<RunSummary>, IRequireTeamMembership
{
    /// <summary>Only runs of any of these authored workflows; bind <c>?workflowIds=&lt;id&gt;</c>. Omit for any source.</summary>
    public IReadOnlyList<Guid>? WorkflowIds { get; init; }

    /// <summary>Only runs from any of these open <c>source_type</c> tokens; bind <c>?sourceTypes=manual</c>. Omit for any.</summary>
    public IReadOnlyList<string>? SourceTypes { get; init; }

    /// <summary>Only runs whose launch scope touches any of these repositories; bind <c>?repositoryIds=&lt;id&gt;</c>. Omit for any.</summary>
    public IReadOnlyList<Guid>? RepositoryIds { get; init; }

    /// <summary>Only runs whose launch scope touches any of these projects; bind <c>?projectIds=&lt;id&gt;</c>. Omit for any.</summary>
    public IReadOnlyList<Guid>? ProjectIds { get; init; }

    /// <summary>Only runs launched by any of these users; bind <c>?actorIds=&lt;id&gt;</c>. Omit for any.</summary>
    public IReadOnlyList<Guid>? ActorIds { get; init; }

    /// <summary>Only runs of any of these coarse origin kinds; bind <c>?runKinds=workflow</c>. Omit for any.</summary>
    public IReadOnlyList<string>? RunKinds { get; init; }

    /// <summary>Only task runs of any of these projection modes; bind <c>?projectionKinds=supervisor</c>. Omit for any.</summary>
    public IReadOnlyList<string>? ProjectionKinds { get; init; }

    /// <summary>Only runs that used any of these agent personas; bind <c>?agentDefinitionIds=&lt;id&gt;</c>. Omit for any.</summary>
    public IReadOnlyList<Guid>? AgentDefinitionIds { get; init; }

    /// <summary>The caller's local start-of-day — runs created at/after this instant count toward <see cref="RunSummary.Today"/>.</summary>
    public DateTimeOffset Today { get; init; }

    /// <summary>Fold the bound scope fields into the run-neutral spec the service applies (no status/pagination — those are computed).</summary>
    public RunListFilter ToFilter() => new()
    {
        WorkflowIds = WorkflowIds,
        SourceTypes = SourceTypes,
        RepositoryIds = RepositoryIds,
        ProjectIds = ProjectIds,
        ActorIds = ActorIds,
        RunKinds = RunKinds,
        ProjectionKinds = ProjectionKinds,
        AgentDefinitionIds = AgentDefinitionIds,
    };
}
