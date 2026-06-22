using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Workflows;

/// <summary>
/// The team's runs index — every TOP-LEVEL run the team owns, newest first, across all sources (authored workflow,
/// snapshot, task). Team-scoped (<see cref="IRequireTeamMembership"/>); the team comes from <c>ICurrentTeam</c>, never
/// the wire. Nested runs (a sub-workflow child, a supervisor-spawned agent run) are excluded — they surface inside
/// their parent's Run Room, not as their own index row.
///
/// <para>The generic filterable, keyset-paginated runs API. The filter fields bind from the query string and fold
/// into a <see cref="RunListFilter"/> (<see cref="ToFilter"/>); every runs surface hits this one query with a
/// different subset of filters. <see cref="Cursor"/> drives keyset pagination.</para>
/// </summary>
public sealed record ListTeamRunsQuery : IQuery<RunPage>, IRequireTeamMembership
{
    /// <summary>Only runs of any of these authored workflows; bind <c>?workflowIds=&lt;id&gt;&amp;workflowIds=&lt;id&gt;</c>. Omit for any source.</summary>
    public IReadOnlyList<Guid>? WorkflowIds { get; init; }

    /// <summary>Only runs in any of these lifecycle states; bind <c>?statuses=Running&amp;statuses=Suspended</c>. Omit for any state.</summary>
    public IReadOnlyList<WorkflowRunStatus>? Statuses { get; init; }

    /// <summary>Only runs from any of these open <c>source_type</c> tokens; bind <c>?sourceTypes=manual&amp;sourceTypes=replay</c>. Omit for any source.</summary>
    public IReadOnlyList<string>? SourceTypes { get; init; }

    /// <summary>Only runs whose launch scope touches any of these repositories; bind <c>?repositoryIds=&lt;id&gt;&amp;repositoryIds=&lt;id&gt;</c>. Omit for any.</summary>
    public IReadOnlyList<Guid>? RepositoryIds { get; init; }

    /// <summary>Only runs whose launch scope touches any of these projects; bind <c>?projectIds=&lt;id&gt;</c>. Omit for any.</summary>
    public IReadOnlyList<Guid>? ProjectIds { get; init; }

    /// <summary>Only runs launched by any of these users; bind <c>?actorIds=&lt;id&gt;</c>. Omit for any.</summary>
    public IReadOnlyList<Guid>? ActorIds { get; init; }

    /// <summary>Only runs with (<c>true</c>) / without (<c>false</c>) a pending decision; omit for either.</summary>
    public bool? HasPendingDecision { get; init; }

    /// <summary>Only runs that need attention (<c>true</c>) / don't (<c>false</c>) — the broad union; omit for either.</summary>
    public bool? NeedsAttention { get; init; }

    /// <summary>Inclusive lower bound on the run's creation instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound on the run's creation instant.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Opaque keyset cursor from the previous page's <c>NextCursor</c>; null/absent = first page.</summary>
    public string? Cursor { get; init; }

    public int Limit { get; init; } = 50;

    /// <summary>Fold the bound filter fields into the run-neutral spec the service applies.</summary>
    public RunListFilter ToFilter() => new()
    {
        WorkflowIds = WorkflowIds,
        Statuses = Statuses,
        SourceTypes = SourceTypes,
        RepositoryIds = RepositoryIds,
        ProjectIds = ProjectIds,
        ActorIds = ActorIds,
        HasPendingDecision = HasPendingDecision,
        NeedsAttention = NeedsAttention,
        Since = Since,
        Until = Until,
    };
}
