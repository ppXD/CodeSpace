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
    /// <summary>Only runs of this authored workflow (omit for any source).</summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>Only runs in any of these lifecycle states; bind <c>?statuses=Running&amp;statuses=Suspended</c>. Omit for any state.</summary>
    public IReadOnlyList<WorkflowRunStatus>? Statuses { get; init; }

    /// <summary>Only runs from this open <c>source_type</c> token.</summary>
    public string? SourceType { get; init; }

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
        WorkflowId = WorkflowId,
        Statuses = Statuses,
        SourceType = SourceType,
        Since = Since,
        Until = Until,
    };
}
